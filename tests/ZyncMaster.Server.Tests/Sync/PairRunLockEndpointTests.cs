using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ZyncMaster.Core;
using Xunit;

namespace ZyncMaster.Server.Tests.Sync;

// Run-lock concurrency tests (plan v2 §B-1) exercised through the real /push endpoint.
// The lock is the production EfSyncRunLock over the harness's shared in-memory SQLite
// connection, so the atomic acquire is genuinely contended across two parallel requests.
public sealed class PairRunLockEndpointTests
{
    // Writer that blocks inside MirrorAsync until the test releases it, so the first request
    // is provably still holding the lock when the second arrives.
    private sealed class GatedWriter : ICalendarWriter
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls;
        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();

        public Task<IReadOnlyList<CalendarOption>> ListCalendarsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CalendarOption>>(Array.Empty<CalendarOption>());

        public Task<CalendarOption> CreateCalendarAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(new CalendarOption { Id = "new", DisplayName = name });

        public async Task<MirrorResult> MirrorAsync(
            string calendarId, IReadOnlyList<AppointmentRecord> records, int reminderMinutes,
            DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default, string pairId = "")
        {
            Interlocked.Increment(ref Calls);
            _entered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            return new MirrorResult { Created = records.Count };
        }
    }

    private sealed class CountingWriter : ICalendarWriter
    {
        public int Calls;
        public Task<IReadOnlyList<CalendarOption>> ListCalendarsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CalendarOption>>(Array.Empty<CalendarOption>());
        public Task<CalendarOption> CreateCalendarAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(new CalendarOption { Id = "new", DisplayName = name });
        public Task<MirrorResult> MirrorAsync(
            string calendarId, IReadOnlyList<AppointmentRecord> records, int reminderMinutes,
            DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default, string pairId = "")
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new MirrorResult { Created = records.Count });
        }
    }

    private static WebApplicationFactory<Program> Build(ICalendarWriter writer) =>
        new ServerTestFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ProviderRegistry>();
                services.AddSingleton(new ProviderRegistry(
                    readerFactory: _ => null!,
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
                // ISyncRunLock stays the production EfSyncRunLock over the shared SQLite conn.
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

    private static async Task<string> SeedPairAsync(WebApplicationFactory<Program> factory)
    {
        var store = factory.Services.GetRequiredService<ISyncPairStore>();
        var id = Guid.NewGuid().ToString("N");
        await store.AddAsync(new SyncPair
        {
            Id = id,
            Name = "Pair",
            Source = new Endpoint { Provider = "OutlookCom", AccountRef = "default", CalendarId = "src-cal" },
            Destination = new Endpoint { Provider = "MicrosoftGraph", AccountRef = "default", CalendarId = "dst-cal" },
            IntervalMin = 15,
        });
        return id;
    }

    private static AppointmentRecord MakeEvent(string id) =>
        new() { Id = id, Subject = id, StartOffset = DateTimeOffset.UtcNow.AddDays(1), EndOffset = DateTimeOffset.UtcNow.AddDays(1).AddHours(1), StartTimeZoneId = "UTC" };

    private static HttpRequestMessage Push(string id, string apiKey) =>
        new(HttpMethod.Post, $"/api/pairs/{id}/push")
        {
            Content = JsonContent.Create(new { events = new[] { MakeEvent("a") } }),
            Headers = { { "X-Api-Key", apiKey } },
        };

    [Fact]
    public async Task Two_concurrent_pushes_only_one_runs_the_other_409s()
    {
        var writer = new GatedWriter();
        var factory = Build(writer);
        var client = await AuthedClientAsync(factory);
        var apiKey = client.DefaultRequestHeaders.GetValues("X-Api-Key").First();
        var id = await SeedPairAsync(factory);

        // First request enters the mirror and holds the lock.
        var first = client.SendAsync(Push(id, apiKey));
        await writer.Entered; // the lock is now held

        // Second request arrives while the lock is held → must be rejected with 409.
        var second = await client.SendAsync(Push(id, apiKey));
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await second.Content.ReadAsStringAsync();
        body.Should().Contain("run_in_progress");

        // Let the first finish.
        writer.Release();
        var firstResp = await first;
        firstResp.StatusCode.Should().Be(HttpStatusCode.OK);

        writer.Calls.Should().Be(1, "only one of the two concurrent pushes may run the mirror");
    }

    [Fact]
    public async Task Lock_is_released_so_a_later_push_succeeds()
    {
        var writer = new CountingWriter();
        var factory = Build(writer);
        var client = await AuthedClientAsync(factory);
        var apiKey = client.DefaultRequestHeaders.GetValues("X-Api-Key").First();
        var id = await SeedPairAsync(factory);

        var first = await client.SendAsync(Push(id, apiKey));
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        // The lock was released in finally, so a sequential second push runs too.
        var second = await client.SendAsync(Push(id, apiKey));
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        writer.Calls.Should().Be(2);
    }
}
