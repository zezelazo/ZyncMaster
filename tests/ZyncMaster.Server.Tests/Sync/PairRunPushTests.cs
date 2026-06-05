using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;
using ZyncMaster.Core;
using ZyncMaster.Server.Data;
using Xunit;

namespace ZyncMaster.Server.Tests.Sync;

public class PairRunPushTests : IClassFixture<ServerTestFactory>
{
    private readonly ServerTestFactory _factory;

    public PairRunPushTests(ServerTestFactory factory) => _factory = factory;

    private sealed class RecordingWriter : ICalendarWriter
    {
        public List<(string calendarId, IReadOnlyList<AppointmentRecord> records, int reminder, DateTimeOffset from, DateTimeOffset to)> Calls { get; } = new();

        public Task<IReadOnlyList<CalendarOption>> ListCalendarsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CalendarOption>>(Array.Empty<CalendarOption>());

        public Task<CalendarOption> CreateCalendarAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(new CalendarOption { Id = "new", DisplayName = name });

        public Task<MirrorResult> MirrorAsync(
            string calendarId, IReadOnlyList<AppointmentRecord> records, int reminderMinutes,
            DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default, string pairId = "")
        {
            Calls.Add((calendarId, records, reminderMinutes, fromUtc, toUtc));
            return Task.FromResult(new MirrorResult { Created = records.Count });
        }
    }

    private sealed class RecordingReader : ICalendarReader
    {
        public List<AppointmentRecord> Window { get; set; } = new();
        public List<(string calendarId, DateTimeOffset from, DateTimeOffset to)> Calls { get; } = new();

        public Task<IReadOnlyList<CalendarOption>> ListCalendarsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CalendarOption>>(Array.Empty<CalendarOption>());

        public Task<IReadOnlyList<AppointmentRecord>> ReadWindowAsync(
            string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc,
            CancellationToken ct = default, bool preserveLocalTime = false)
        {
            Calls.Add((calendarId, fromUtc, toUtc));
            return Task.FromResult<IReadOnlyList<AppointmentRecord>>(Window);
        }
    }

    private static WebApplicationFactory<Program> Build(RecordingReader? reader, RecordingWriter writer) =>
        new ServerTestFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ProviderRegistry>();
                services.AddSingleton(new ProviderRegistry(
                    readerFactory: _ => reader ?? new RecordingReader(),
                    writerFactory: _ => writer));

                services.RemoveAll<IConnectedAccountStore>();
                services.AddSingleton<IConnectedAccountStore>(_ =>
                {
                    var store = new DataProtectionConnectedAccountStore(DataProtectionProvider.Create("tests"));
                    store.SetAsync("default", "rt").GetAwaiter().GetResult();
                    return store;
                });

                services.RemoveAll<ISyncPairStore>();
                services.AddSingleton<ISyncPairStore, InMemorySyncPairStore>();
            });
        });

    private static async Task<HttpClient> AuthedClientAsync(WebApplicationFactory<Program> factory)
    {
        var store = factory.Services.GetRequiredService<IDeviceStore>();
        var key = ApiKeyGenerator.Generate();
        await store.AddAsync(new Device
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Laptop",
            ApiKeyHash = ApiKeyHasher.Hash(key),
            CreatedUtc = DateTimeOffset.UtcNow,
        });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    private static async Task<string> SeedPairAsync(
        WebApplicationFactory<Program> factory, string sourceProvider)
    {
        var store = factory.Services.GetRequiredService<ISyncPairStore>();
        var id = Guid.NewGuid().ToString("N");
        await store.AddAsync(new SyncPair
        {
            Id = id,
            Name = "Pair",
            Source = new Endpoint { Provider = sourceProvider, AccountRef = "default", CalendarId = "src-cal" },
            Destination = new Endpoint { Provider = "MicrosoftGraph", AccountRef = "default", CalendarId = "dst-cal" },
            IntervalMin = 15,
        });
        return id;
    }

    private static AppointmentRecord MakeEvent(string id) =>
        new() { Id = id, Subject = id, StartOffset = DateTimeOffset.UtcNow.AddDays(1), EndOffset = DateTimeOffset.UtcNow.AddDays(1).AddHours(1), StartTimeZoneId = "UTC" };

    [Fact]
    public async Task Push_mirrors_events_into_destination_and_records_result()
    {
        var writer = new RecordingWriter();
        var factory = Build(null, writer);
        var client = await AuthedClientAsync(factory);
        var id = await SeedPairAsync(factory, "OutlookCom");

        var resp = await client.PostAsJsonAsync($"/api/pairs/{id}/push", new
        {
            events = new[] { MakeEvent("a"), MakeEvent("b") },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("created").GetInt32().Should().Be(2);

        writer.Calls.Should().ContainSingle();
        writer.Calls[0].calendarId.Should().Be("dst-cal");
        writer.Calls[0].records.Should().HaveCount(2);
        writer.Calls[0].reminder.Should().Be(30);

        var pair = await factory.Services.GetRequiredService<ISyncPairStore>().GetAsync(id);
        pair!.LastResult!.Created.Should().Be(2);
        pair.LastRunUtc.Should().NotBeNull();
    }

    // FIX C — a /push from a device renews that device's lease (LeaseUntil + LastSeenUtc). Without
    // this the lease set at register lapses and the cron `covered` set is always empty, so cron
    // would double-run the user's pairs alongside the active App. We register a real keyId.secret
    // device, push under its key, and assert both fields advanced past their pre-push values.
    [Fact]
    public async Task Push_renews_calling_devices_lease_and_last_seen()
    {
        var writer = new RecordingWriter();
        var factory = Build(null, writer);

        // Register a device with a known id and a stale lease/last-seen so the renewal is observable.
        var deviceId = Guid.NewGuid().ToString("N");
        var generated = ApiKeyGenerator.GenerateKey();
        var stale = DateTimeOffset.UtcNow.AddHours(-1);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
            db.Devices.Add(new DeviceRow
            {
                Id = deviceId,
                UserId = DefaultCurrentUserAccessor.DefaultUserId,
                Name = "Laptop",
                ApiKeyHash = ApiKeyHasher.Hash(generated.Secret),
                KeyId = generated.KeyId,
                CreatedUtc = stale,
                LastSeenUtc = stale,
                LeaseUntil = stale,
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", generated.ApiKey);
        var id = await SeedPairAsync(factory, "OutlookCom");

        var resp = await client.PostAsJsonAsync($"/api/pairs/{id}/push", new
        {
            events = new[] { MakeEvent("a") },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        var row = await verifyDb.Devices.FirstAsync(d => d.Id == deviceId);
        row.LeaseUntil.Should().NotBeNull();
        row.LeaseUntil!.Value.Should().BeAfter(DateTimeOffset.UtcNow, "the push must extend the lease into the future");
        row.LastSeenUtc.Should().NotBeNull();
        row.LastSeenUtc!.Value.Should().BeAfter(stale, "the push must bump LastSeenUtc off its stale value");
    }

    [Fact]
    public async Task Push_unknown_pair_returns_404()
    {
        var writer = new RecordingWriter();
        var factory = Build(null, writer);
        var client = await AuthedClientAsync(factory);

        var resp = await client.PostAsJsonAsync("/api/pairs/missing/push", new { events = Array.Empty<AppointmentRecord>() });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Push_requires_api_key()
    {
        var factory = Build(null, new RecordingWriter());
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/pairs/x/push", new { events = Array.Empty<AppointmentRecord>() });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Run_reads_source_then_mirrors_to_destination()
    {
        var reader = new RecordingReader { Window = new() { MakeEvent("x"), MakeEvent("y"), MakeEvent("z") } };
        var writer = new RecordingWriter();
        var factory = Build(reader, writer);
        var client = await AuthedClientAsync(factory);
        var id = await SeedPairAsync(factory, "MicrosoftGraph");

        var resp = await client.PostAsync($"/api/pairs/{id}/run", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("created").GetInt32().Should().Be(3);

        reader.Calls.Should().ContainSingle().Which.calendarId.Should().Be("src-cal");
        writer.Calls.Should().ContainSingle();
        writer.Calls[0].calendarId.Should().Be("dst-cal");
        writer.Calls[0].records.Should().HaveCount(3);

        var pair = await factory.Services.GetRequiredService<ISyncPairStore>().GetAsync(id);
        pair!.LastResult!.Created.Should().Be(3);
        pair.LastRunUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Run_with_outlookcom_source_returns_409_no_reader()
    {
        var factory = Build(null, new RecordingWriter());
        var client = await AuthedClientAsync(factory);
        var id = await SeedPairAsync(factory, "OutlookCom");

        var resp = await client.PostAsync($"/api/pairs/{id}/run", null);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetString().Should().Be("no_server_reader");
    }

    [Fact]
    public async Task Run_unknown_pair_returns_404()
    {
        var factory = Build(new RecordingReader(), new RecordingWriter());
        var client = await AuthedClientAsync(factory);

        var resp = await client.PostAsync("/api/pairs/missing/run", null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
