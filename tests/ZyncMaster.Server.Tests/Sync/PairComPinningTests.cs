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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ZyncMaster.Core;
using ZyncMaster.Server.Data;
using Xunit;

namespace ZyncMaster.Server.Tests.Sync;

// Track B — COM device-pinning server surface: claim-on-first-push, the /request-sync signal
// (and its outcomes), RecordRunAsync clearing the signal, the GET /api/pairs enrichment with the
// pinned device's name + online flag, and cross-user isolation.
public class PairComPinningTests : IClassFixture<ServerTestFactory>
{
    private readonly ServerTestFactory _factory;

    public PairComPinningTests(ServerTestFactory factory) => _factory = factory;

    private sealed class StubReader : ICalendarReader
    {
        public Task<IReadOnlyList<CalendarOption>> ListCalendarsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CalendarOption>>(Array.Empty<CalendarOption>());
        public Task<IReadOnlyList<AppointmentRecord>> ReadWindowAsync(
            string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc,
            CancellationToken ct = default, bool preserveLocalTime = false) =>
            Task.FromResult<IReadOnlyList<AppointmentRecord>>(Array.Empty<AppointmentRecord>());
    }

    private sealed class RecordingWriter : ICalendarWriter
    {
        // Number of times MirrorAsync ran across ALL instances this registry produced. The push guard
        // test asserts the destructive mirror NEVER ran for a rejected (wrong-device) push.
        public static int MirrorCalls;
        public Task<IReadOnlyList<CalendarOption>> ListCalendarsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CalendarOption>>(Array.Empty<CalendarOption>());
        public Task<CalendarOption> CreateCalendarAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(new CalendarOption { Id = "new", DisplayName = name });
        public Task<MirrorResult> MirrorAsync(
            string calendarId, IReadOnlyList<AppointmentRecord> records, int reminderMinutes,
            DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default, string pairId = "")
        {
            System.Threading.Interlocked.Increment(ref MirrorCalls);
            return Task.FromResult(new MirrorResult { Created = records.Count });
        }
    }

    private static WebApplicationFactory<Program> Build() =>
        new ServerTestFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // The cookie sign-in flow (CookieAuthHelper) drives the real OAuth callback against
                // this fake identity token service; without it the callback 500s. Empty UPN keeps the
                // connected-account write on the "default" key.
                services.RemoveAll<IMicrosoftTokenService>();
                services.AddSingleton<IMicrosoftTokenService>(
                    new CookieAuthHelper.FakeIdentityTokenService { Upn = "" });

                services.RemoveAll<ProviderRegistry>();
                services.AddSingleton(new ProviderRegistry(
                    readerFactory: _ => new StubReader(),
                    writerFactory: _ => new RecordingWriter()));

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

    // A device whose key carries the deviceId claim (keyId.secret), seeded under the default user
    // with the given lease so its online state is controllable. Returns (deviceId, apiKey).
    private static async Task<(string deviceId, string apiKey)> SeedDeviceAsync(
        WebApplicationFactory<Program> factory, string name, DateTimeOffset? leaseUntil)
    {
        var deviceId = Guid.NewGuid().ToString("N");
        var generated = ApiKeyGenerator.GenerateKey();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        db.Devices.Add(new DeviceRow
        {
            Id = deviceId,
            UserId = DefaultCurrentUserAccessor.DefaultUserId,
            Name = name,
            ApiKeyHash = ApiKeyHasher.Hash(generated.Secret),
            KeyId = generated.KeyId,
            CreatedUtc = DateTimeOffset.UtcNow,
            LastSeenUtc = leaseUntil,
            LeaseUntil = leaseUntil,
        });
        await db.SaveChangesAsync();
        return (deviceId, generated.ApiKey);
    }

    private static async Task<string> SeedPairAsync(
        WebApplicationFactory<Program> factory, string sourceProvider, string? pinnedDeviceId = null)
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
            PinnedDeviceId = pinnedDeviceId,
        });
        return id;
    }

    private static AppointmentRecord MakeEvent(string id) =>
        new() { Id = id, Subject = id, StartOffset = DateTimeOffset.UtcNow.AddDays(1), EndOffset = DateTimeOffset.UtcNow.AddDays(1).AddHours(1), StartTimeZoneId = "UTC" };

    private static HttpClient KeyedClient(WebApplicationFactory<Program> factory, string apiKey)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }

    // ---- claim-on-first-push -------------------------------------------------------------------

    [Fact]
    public async Task First_push_claims_an_unpinned_com_pair_to_the_pushing_device()
    {
        var factory = Build();
        var (deviceId, apiKey) = await SeedDeviceAsync(factory, "Laptop", DateTimeOffset.UtcNow.AddMinutes(5));
        var id = await SeedPairAsync(factory, "OutlookCom", pinnedDeviceId: null);

        var resp = await KeyedClient(factory, apiKey).PostAsJsonAsync($"/api/pairs/{id}/push", new
        {
            events = new[] { MakeEvent("a") },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var pair = await factory.Services.GetRequiredService<ISyncPairStore>().GetAsync(id);
        pair!.PinnedDeviceId.Should().Be(deviceId, "the first push claims the unpinned COM pair");
    }

    [Fact]
    public async Task First_push_does_not_pin_a_non_com_pair()
    {
        var factory = Build();
        var (_, apiKey) = await SeedDeviceAsync(factory, "Laptop", DateTimeOffset.UtcNow.AddMinutes(5));
        // A Graph<->Graph pair has no COM side: pushing it must NOT set a pin.
        var id = await SeedPairAsync(factory, "MicrosoftGraph", pinnedDeviceId: null);

        var resp = await KeyedClient(factory, apiKey).PostAsJsonAsync($"/api/pairs/{id}/push", new
        {
            events = new[] { MakeEvent("a") },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var pair = await factory.Services.GetRequiredService<ISyncPairStore>().GetAsync(id);
        pair!.PinnedDeviceId.Should().BeNull();
    }

    [Fact]
    public async Task Push_from_a_different_device_to_an_already_pinned_pair_is_rejected_and_leaves_the_pin()
    {
        var factory = Build();
        var (_, apiKey) = await SeedDeviceAsync(factory, "Laptop", DateTimeOffset.UtcNow.AddMinutes(5));
        var id = await SeedPairAsync(factory, "OutlookCom", pinnedDeviceId: "other-device");

        // The pair is pinned to "other-device"; this device is NOT it, so the push is rejected with
        // 409 pinned_to_other_device (the device-pin guard) and the pin is left untouched.
        var resp = await KeyedClient(factory, apiKey).PostAsJsonAsync($"/api/pairs/{id}/push", new
        {
            events = new[] { MakeEvent("a") },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetString().Should().Be("pinned_to_other_device");

        var pair = await factory.Services.GetRequiredService<ISyncPairStore>().GetAsync(id);
        pair!.PinnedDeviceId.Should().Be("other-device", "a rejected push never re-pins an already pinned pair");
    }

    // ---- createPair pin ------------------------------------------------------------------------

    [Fact]
    public async Task Create_com_pair_with_explicit_pin_persists_it()
    {
        var factory = Build();
        var client = await CookieAuthHelper.SignInAsync(factory);

        var body = new
        {
            name = "Pinned",
            source = new { provider = "OutlookCom", calendarId = "src", calendarName = "Src" },
            destination = new { provider = "MicrosoftGraph", accountRef = "default", calendarId = "dst", calendarName = "Dst" },
            intervalMin = 15,
            pinnedDeviceId = "dev-xyz",
        };
        var create = await client.PostAsJsonAsync("/api/pairs", body);
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("pinnedDeviceId").GetString().Should().Be("dev-xyz");
    }

    // ---- request-sync --------------------------------------------------------------------------

    [Fact]
    public async Task RequestSync_unknown_pair_returns_404()
    {
        var factory = Build();
        var (_, apiKey) = await SeedDeviceAsync(factory, "Laptop", DateTimeOffset.UtcNow.AddMinutes(5));

        var resp = await KeyedClient(factory, apiKey).PostAsync("/api/pairs/missing/request-sync", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RequestSync_on_non_com_pair_returns_409_not_com_pinned()
    {
        var factory = Build();
        var (_, apiKey) = await SeedDeviceAsync(factory, "Laptop", DateTimeOffset.UtcNow.AddMinutes(5));
        var id = await SeedPairAsync(factory, "MicrosoftGraph");

        var resp = await KeyedClient(factory, apiKey).PostAsync($"/api/pairs/{id}/request-sync", null);
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetString().Should().Be("not_com_pinned");
    }

    [Fact]
    public async Task RequestSync_when_origin_offline_returns_origin_unavailable()
    {
        var factory = Build();
        // Pinned device exists but its lease has expired -> offline.
        var (pinnedId, _) = await SeedDeviceAsync(factory, "OldPC", DateTimeOffset.UtcNow.AddMinutes(-1));
        // The caller is a DIFFERENT device (online), so it is not the pinned one.
        var (_, callerKey) = await SeedDeviceAsync(factory, "Caller", DateTimeOffset.UtcNow.AddMinutes(5));
        var id = await SeedPairAsync(factory, "OutlookCom", pinnedDeviceId: pinnedId);

        var resp = await KeyedClient(factory, callerKey).PostAsync($"/api/pairs/{id}/request-sync", null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("origin_unavailable");
        doc.RootElement.GetProperty("device").GetString().Should().Be("OldPC");

        var pair = await factory.Services.GetRequiredService<ISyncPairStore>().GetAsync(id);
        pair!.SyncRequestedUtc.Should().BeNull("no signal is stamped when the origin cannot service it");
    }

    [Fact]
    public async Task RequestSync_when_origin_online_stamps_signal_and_returns_requested()
    {
        var factory = Build();
        var (pinnedId, _) = await SeedDeviceAsync(factory, "HomePC", DateTimeOffset.UtcNow.AddMinutes(5));
        var (_, callerKey) = await SeedDeviceAsync(factory, "Caller", DateTimeOffset.UtcNow.AddMinutes(5));
        var id = await SeedPairAsync(factory, "OutlookCom", pinnedDeviceId: pinnedId);

        var resp = await KeyedClient(factory, callerKey).PostAsync($"/api/pairs/{id}/request-sync", null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("requested");
        doc.RootElement.GetProperty("device").GetString().Should().Be("HomePC");

        var pair = await factory.Services.GetRequiredService<ISyncPairStore>().GetAsync(id);
        pair!.SyncRequestedUtc.Should().NotBeNull("an online origin gets a stamped signal");
    }

    [Fact]
    public async Task RequestSync_by_the_pinned_device_returns_local()
    {
        var factory = Build();
        // The caller IS the pinned device (same id, online).
        var (pinnedId, apiKey) = await SeedDeviceAsync(factory, "ThisPC", DateTimeOffset.UtcNow.AddMinutes(5));
        var id = await SeedPairAsync(factory, "OutlookCom", pinnedDeviceId: pinnedId);

        var resp = await KeyedClient(factory, apiKey).PostAsync($"/api/pairs/{id}/request-sync", null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("local");

        var pair = await factory.Services.GetRequiredService<ISyncPairStore>().GetAsync(id);
        pair!.SyncRequestedUtc.Should().BeNull("the pinned device runs locally instead of signalling itself");
    }

    [Fact]
    public async Task RequestSync_on_unpinned_com_pair_returns_origin_unavailable()
    {
        var factory = Build();
        var (_, apiKey) = await SeedDeviceAsync(factory, "Caller", DateTimeOffset.UtcNow.AddMinutes(5));
        var id = await SeedPairAsync(factory, "OutlookCom", pinnedDeviceId: null);

        var resp = await KeyedClient(factory, apiKey).PostAsync($"/api/pairs/{id}/request-sync", null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("origin_unavailable");
    }

    // ---- RecordRunAsync clears the signal -------------------------------------------------------

    [Fact]
    public async Task A_push_clears_a_pending_sync_signal()
    {
        var factory = Build();
        var (pinnedId, apiKey) = await SeedDeviceAsync(factory, "ThisPC", DateTimeOffset.UtcNow.AddMinutes(5));
        var store = factory.Services.GetRequiredService<ISyncPairStore>();
        var id = await SeedPairAsync(factory, "OutlookCom", pinnedDeviceId: pinnedId);

        // Stamp a pending signal in the past so the recorded run (now) is >= it.
        var existing = await store.GetAsync(id);
        await store.UpdateAsync(existing! with { SyncRequestedUtc = DateTimeOffset.UtcNow.AddSeconds(-5) });

        var resp = await KeyedClient(factory, apiKey).PostAsJsonAsync($"/api/pairs/{id}/push", new
        {
            events = new[] { MakeEvent("a") },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var pair = await store.GetAsync(id);
        pair!.SyncRequestedUtc.Should().BeNull("a recorded run at/after the request consumes the signal");
        pair.LastRunUtc.Should().NotBeNull();
    }

    // ---- /push device-pin guard ----------------------------------------------------------------

    [Fact]
    public async Task Push_from_a_different_device_to_a_pinned_pair_is_rejected_without_mirroring()
    {
        RecordingWriter.MirrorCalls = 0;
        var factory = Build();
        // Both devices belong to the same (default) user, so the user-scoped store resolves the pair
        // for either one — the rejection is the device-pin guard, not user scoping.
        var (deviceA, keyA) = await SeedDeviceAsync(factory, "DevA", DateTimeOffset.UtcNow.AddMinutes(5));
        var (_, keyB) = await SeedDeviceAsync(factory, "DevB", DateTimeOffset.UtcNow.AddMinutes(5));
        var id = await SeedPairAsync(factory, "OutlookCom", pinnedDeviceId: null);

        // Dev-A pushes first and claims the unpinned COM pair (claim-on-first-push).
        var pushA = await KeyedClient(factory, keyA).PostAsJsonAsync($"/api/pairs/{id}/push", new
        {
            events = new[] { MakeEvent("a") },
        });
        pushA.StatusCode.Should().Be(HttpStatusCode.OK);

        var store = factory.Services.GetRequiredService<ISyncPairStore>();
        (await store.GetAsync(id))!.PinnedDeviceId.Should().Be(deviceA);
        RecordingWriter.MirrorCalls.Should().Be(1, "dev-A's push mirrored once");

        // Dev-B now pushes the SAME pair: it is pinned to dev-A, so the server rejects with 409
        // pinned_to_other_device BEFORE the run-lock and the destructive mirror.
        var pushB = await KeyedClient(factory, keyB).PostAsJsonAsync($"/api/pairs/{id}/push", new
        {
            events = new[] { MakeEvent("b") },
        });
        pushB.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var doc = JsonDocument.Parse(await pushB.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetString().Should().Be("pinned_to_other_device");

        // The pin is unchanged and dev-B's events were NOT mirrored (no second MirrorAsync call).
        (await store.GetAsync(id))!.PinnedDeviceId.Should().Be(deviceA, "the rejected push never re-pins");
        RecordingWriter.MirrorCalls.Should().Be(1, "the wrong-device push never reached the mirror");
    }

    // ---- COM detection rule (source-only / OrdinalIgnoreCase) ----------------------------------

    [Theory]
    [InlineData("OutlookCom", true)]      // canonical source COM
    [InlineData("outlookcom", true)]      // OrdinalIgnoreCase
    [InlineData("OUTLOOKCOM", true)]
    [InlineData("MicrosoftGraph", false)] // Graph source is not COM-pinned
    public void IsComPinnedPair_keys_only_on_the_source_provider(string sourceProvider, bool expected)
    {
        var pair = new SyncPair
        {
            Id = "p",
            Name = "Pair",
            Source = new Endpoint { Provider = sourceProvider, CalendarId = "src" },
            Destination = new Endpoint { Provider = "MicrosoftGraph", CalendarId = "dst" },
        };
        PairEndpoints.IsComPinnedPair(pair).Should().Be(expected);
    }

    [Fact]
    public void IsComPinnedPair_ignores_the_destination_provider()
    {
        // A (non-existent in practice) COM destination must NOT make a Graph-sourced pair COM-pinned:
        // detection is source-only so the server and engine agree on the single owning side.
        var pair = new SyncPair
        {
            Id = "p",
            Name = "Pair",
            Source = new Endpoint { Provider = "MicrosoftGraph", CalendarId = "src" },
            Destination = new Endpoint { Provider = "OutlookCom", CalendarId = "dst" },
        };
        PairEndpoints.IsComPinnedPair(pair).Should().BeFalse();
    }

    // ---- cross-user request-sync isolation -----------------------------------------------------

    [Fact]
    public async Task RequestSync_on_another_users_pair_returns_404()
    {
        // The default Build() uses an InMemorySyncPairStore (not user-scoped), so this test stands up
        // its own factory on the REAL EF stores with a switchable identity to exercise user scoping.
        var fake = new CookieAuthHelper.FakeIdentityTokenService();
        var factory = new ServerTestFactory().WithFakeIdentity(fake);

        // User A signs in and creates a COM-pinned pair.
        fake.Subject = "oid-a"; fake.Upn = "alice@test"; fake.DisplayName = "Alice"; fake.RefreshToken = "rt-a";
        var aClient = await CookieAuthHelper.SignInAsync(factory);
        var create = await aClient.PostAsJsonAsync("/api/pairs", new
        {
            name = "A pair",
            source = new { provider = "OutlookCom", calendarId = "src", calendarName = "Src" },
            destination = new { provider = "MicrosoftGraph", accountRef = "alice@test", calendarId = "dst", calendarName = "Dst" },
            intervalMin = 15,
        });
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var aPairId = doc.RootElement.GetProperty("id").GetString()!;

        // User B signs in and posts /request-sync against A's pair id. The user-scoped store resolves
        // null under B -> 404 (never 403, never a leak of the pair's existence).
        fake.Subject = "oid-b"; fake.Upn = "bob@test"; fake.DisplayName = "Bob"; fake.RefreshToken = "rt-b";
        var bClient = await CookieAuthHelper.SignInAsync(factory);
        var resp = await bClient.PostAsync($"/api/pairs/{aPairId}/request-sync", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- GET enrichment ------------------------------------------------------------------------

    [Fact]
    public async Task Pair_listing_enriches_com_pinned_pairs_with_device_name_and_online_flag()
    {
        var factory = Build();
        var client = await CookieAuthHelper.SignInAsync(factory);

        // Resolve the cookie user's id so the pinned device is seeded under the SAME user the
        // listing runs as (the device store is user-scoped).
        var userId = await ResolveCookieUserIdAsync(factory);
        var deviceId = Guid.NewGuid().ToString("N");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
            db.Devices.Add(new DeviceRow
            {
                Id = deviceId,
                UserId = userId,
                Name = "DeskPC",
                ApiKeyHash = "h",
                CreatedUtc = DateTimeOffset.UtcNow,
                LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(5),
            });
            await db.SaveChangesAsync();
        }

        await SeedPairAsync(factory, "OutlookCom", pinnedDeviceId: deviceId);

        var list = await client.GetFromJsonAsync<JsonElement>("/api/pairs");
        var pair = list.EnumerateArray().Single();
        pair.GetProperty("pinnedDeviceId").GetString().Should().Be(deviceId);
        pair.GetProperty("pinnedDeviceName").GetString().Should().Be("DeskPC");
        pair.GetProperty("pinnedDeviceOnline").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Pair_listing_marks_pinned_device_offline_when_lease_expired()
    {
        var factory = Build();
        var client = await CookieAuthHelper.SignInAsync(factory);

        var userId = await ResolveCookieUserIdAsync(factory);
        var deviceId = Guid.NewGuid().ToString("N");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
            db.Devices.Add(new DeviceRow
            {
                Id = deviceId,
                UserId = userId,
                Name = "DeskPC",
                ApiKeyHash = "h",
                CreatedUtc = DateTimeOffset.UtcNow,
                LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(-1),
            });
            await db.SaveChangesAsync();
        }

        await SeedPairAsync(factory, "OutlookCom", pinnedDeviceId: deviceId);

        var list = await client.GetFromJsonAsync<JsonElement>("/api/pairs");
        var pair = list.EnumerateArray().Single();
        pair.GetProperty("pinnedDeviceOnline").GetBoolean().Should().BeFalse();
        pair.GetProperty("pinnedDeviceName").GetString().Should().Be("DeskPC");
    }

    // The cookie sign-in resolves to a real user (provider=microsoft, subject=oid-123). Read its id
    // from the Users table so a seeded device shares the listing's user scope.
    private static async Task<string> ResolveCookieUserIdAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        // The cookie sign-in creates exactly one non-default user. Materialize the candidate rows
        // (SQLite cannot translate the DateTimeOffset ordering) and pick the newest in memory.
        var users = await db.Users
            .Where(u => u.Id != DefaultCurrentUserAccessor.DefaultUserId)
            .ToListAsync();
        return users.OrderByDescending(u => u.CreatedUtc).First().Id;
    }
}
