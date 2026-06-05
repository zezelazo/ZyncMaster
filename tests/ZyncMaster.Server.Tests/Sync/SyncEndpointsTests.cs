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
using ZyncMaster.Core;
using ZyncMaster.Graph;
using Xunit;

namespace ZyncMaster.Server.Tests.Sync;

public class SyncEndpointsTests : IClassFixture<ServerTestFactory>
{
    private readonly ServerTestFactory _factory;

    public SyncEndpointsTests(ServerTestFactory factory) => _factory = factory;

    private sealed class RecordingCalendarTarget : ICalendarTarget
    {
        public List<(string calendarId, EventDraft draft)> Creates { get; } = new();
        public List<(string eventId, EventDraft draft)> Updates { get; } = new();
        public List<string> Deletes { get; } = new();

        public IReadOnlyList<CalendarTargetInfo> Calendars { get; set; } = new[]
        {
            new CalendarTargetInfo { Id = "cal-default", DisplayName = "Calendar", IsDefault = true, Owner = "me" },
        };

        public HashSet<string> ThrowOnCreateExternalIds { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<CalendarTargetInfo>> ListCalendarsAsync(CancellationToken ct = default) =>
            Task.FromResult(Calendars);

        public Task<CalendarTargetInfo> CreateCalendarAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(new CalendarTargetInfo { Id = "cal-new", DisplayName = name });

        public Task<IReadOnlyDictionary<string, ExistingEventLookup>> FindByExternalIdsAsync(
            string calendarId, IReadOnlyList<string> externalIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, ExistingEventLookup>>(
                new Dictionary<string, ExistingEventLookup>(StringComparer.Ordinal));

        public Task<string> CreateEventAsync(string calendarId, EventDraft draft, CancellationToken ct = default)
        {
            if (ThrowOnCreateExternalIds.Contains(draft.ExternalId))
                throw new InvalidOperationException("simulated create failure");
            Creates.Add((calendarId, draft));
            return Task.FromResult("evt-" + draft.ExternalId);
        }

        public Task UpdateEventAsync(string eventId, EventDraft draft, CancellationToken ct = default)
        {
            Updates.Add((eventId, draft));
            return Task.CompletedTask;
        }

        public Task DeleteEventAsync(string eventId, CancellationToken ct = default)
        {
            Deletes.Add(eventId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ManagedEventRef>> ListManagedInWindowAsync(
            string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ManagedEventRef>>(Array.Empty<ManagedEventRef>());
    }

    private static WebApplicationFactory<Program> Build(
        RecordingCalendarTarget target,
        bool seedAccount = true,
        string? deviceTargetCalendarId = null,
        Action<RecordingCalendarTarget>? configure = null) =>
        new ServerTestFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                configure?.Invoke(target);

                services.RemoveAll<Func<string, ICalendarTarget>>();
                services.AddSingleton<Func<string, ICalendarTarget>>(_ => _ => target);

                services.RemoveAll<IConnectedAccountStore>();
                services.AddSingleton<IConnectedAccountStore>(_ =>
                {
                    var store = new DataProtectionConnectedAccountStore(DataProtectionProvider.Create("tests"));
                    if (seedAccount)
                        store.SetAsync("default", "refresh-token").GetAwaiter().GetResult();
                    return store;
                });
            });
        });

    private static async Task<(WebApplicationFactory<Program> factory, string key, string deviceId)> SeedDeviceAsync(
        WebApplicationFactory<Program> factory, string? targetCalendarId = null)
    {
        var store = factory.Services.GetRequiredService<IDeviceStore>();
        var key = ApiKeyGenerator.Generate();
        var device = new Device
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Laptop",
            ApiKeyHash = ApiKeyHasher.Hash(key),
            TargetCalendarId = targetCalendarId,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        await store.AddAsync(device);
        return (factory, key, device.Id);
    }

    private static AppointmentRecord MakeEvent(string id, string subject)
    {
        var start = DateTimeOffset.UtcNow.AddDays(1);
        return new AppointmentRecord
        {
            Id = id,
            Subject = subject,
            StartOffset = start,
            EndOffset = start.AddHours(1),
            StartTimeZoneId = "UTC",
        };
    }

    [Fact]
    public async Task No_api_key_returns_401()
    {
        var target = new RecordingCalendarTarget();
        var factory = Build(target);
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/sync/calendar",
            new SyncRequest { Events = new List<AppointmentRecord>() });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Happy_path_two_new_events_returns_created_2_and_records_creates()
    {
        var target = new RecordingCalendarTarget();
        var factory = Build(target);
        var (_, key, deviceId) = await SeedDeviceAsync(factory);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);

        var resp = await client.PostAsJsonAsync("/api/sync/calendar", new SyncRequest
        {
            Events = new List<AppointmentRecord> { MakeEvent("a", "First"), MakeEvent("b", "Second") },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("created").GetInt32().Should().Be(2);

        target.Creates.Should().HaveCount(2);
        target.Creates.Should().OnlyContain(c => c.calendarId == "cal-default");

        var state = await factory.Services.GetRequiredService<ISyncStateStore>().GetAsync(deviceId);
        state.Should().NotBeNull();
        state!.LastCreated.Should().Be(2);
    }

    [Fact]
    public async Task Device_without_target_calendar_uses_default_calendar()
    {
        var target = new RecordingCalendarTarget
        {
            Calendars = new[]
            {
                new CalendarTargetInfo { Id = "cal-other", DisplayName = "Other", IsDefault = false },
                new CalendarTargetInfo { Id = "cal-primary", DisplayName = "Primary", IsDefault = true },
            },
        };
        var factory = Build(target);
        var (_, key, _) = await SeedDeviceAsync(factory, targetCalendarId: null);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);

        var resp = await client.PostAsJsonAsync("/api/sync/calendar", new SyncRequest
        {
            Events = new List<AppointmentRecord> { MakeEvent("a", "First") },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        target.Creates.Should().ContainSingle().Which.calendarId.Should().Be("cal-primary");
    }

    [Fact]
    public async Task Device_with_target_calendar_uses_it()
    {
        var target = new RecordingCalendarTarget();
        var factory = Build(target);
        var (_, key, _) = await SeedDeviceAsync(factory, targetCalendarId: "cal-explicit");

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);

        var resp = await client.PostAsJsonAsync("/api/sync/calendar", new SyncRequest
        {
            Events = new List<AppointmentRecord> { MakeEvent("a", "First") },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        target.Creates.Should().ContainSingle().Which.calendarId.Should().Be("cal-explicit");
    }

    [Fact]
    public async Task No_connected_account_returns_409()
    {
        var target = new RecordingCalendarTarget();
        var factory = Build(target, seedAccount: false);
        var (_, key, _) = await SeedDeviceAsync(factory);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);

        var resp = await client.PostAsJsonAsync("/api/sync/calendar", new SyncRequest
        {
            Events = new List<AppointmentRecord> { MakeEvent("a", "First") },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetString().Should().Be("no_connected_account");
    }

    [Fact]
    public async Task Null_events_returns_400_bad_request()
    {
        var target = new RecordingCalendarTarget();
        var factory = Build(target);
        var (_, key, _) = await SeedDeviceAsync(factory);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);

        // {"events":null} must not blow up as a 500; it is a malformed body -> 400 bad_request.
        var content = new StringContent("{\"events\":null}", System.Text.Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/api/sync/calendar", content);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetString().Should().Be("bad_request");
        target.Creates.Should().BeEmpty();
    }

    [Fact]
    public async Task Account_without_calendars_returns_409_no_calendar()
    {
        // A connected account that enumerates ZERO calendars must yield 409 no_calendar, not a 500
        // from cals.First() on an empty list. The device has no explicit target calendar, so the
        // service falls into the enumeration path.
        var target = new RecordingCalendarTarget { Calendars = Array.Empty<CalendarTargetInfo>() };
        var factory = Build(target);
        var (_, key, _) = await SeedDeviceAsync(factory, targetCalendarId: null);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);

        var resp = await client.PostAsJsonAsync("/api/sync/calendar", new SyncRequest
        {
            Events = new List<AppointmentRecord> { MakeEvent("a", "First") },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetString().Should().Be("no_calendar");
        target.Creates.Should().BeEmpty();
    }

    [Fact]
    public async Task Partial_failure_returns_200_with_failures()
    {
        var target = new RecordingCalendarTarget();
        var factory = Build(target, configure: t => t.ThrowOnCreateExternalIds.Add("b"));
        var (_, key, _) = await SeedDeviceAsync(factory);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);

        var resp = await client.PostAsJsonAsync("/api/sync/calendar", new SyncRequest
        {
            Events = new List<AppointmentRecord> { MakeEvent("a", "First"), MakeEvent("b", "Second") },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("created").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("failures").GetArrayLength().Should().BeGreaterThan(0);
    }
}
