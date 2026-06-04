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
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ZyncMaster.Core;
using ZyncMaster.Graph;
using Xunit;

namespace ZyncMaster.Server.Tests.Sync;

// End-to-end per-user isolation (WS-C). Two distinct signed-in users (A and B) are driven
// through the real OAuth sign-in flow against a switchable fake identity service, each with
// their own connected account and a device api key bound to their user id. Everything runs
// against the REAL EF stores (the default ServerTestFactory) so the user-scoping filters in
// the stores are actually exercised. The Graph reader/writer is replaced with recording
// doubles that capture the account ref each call resolves, proving a push/run mirrors using
// the CALLER's account rather than a global "default" or the other user's account.
public class CrossUserIsolationTests
{
    // Singleton identity service whose returned identity can be switched between sign-ins.
    // Each /connect -> /connect/callback uses whatever identity is currently set, so the
    // test signs in user A, switches, then signs in user B against the same host.
    private sealed class SwitchableIdentityService : IMicrosoftTokenService
    {
        public string Subject { get; set; } = "oid-a";
        public string Upn { get; set; } = "alice@test";
        public string DisplayName { get; set; } = "Alice";
        public string RefreshToken { get; set; } = "rt-a";

        public void Use(string subject, string upn, string display, string refreshToken)
        {
            Subject = subject;
            Upn = upn;
            DisplayName = display;
            RefreshToken = refreshToken;
        }

        public Task<TokenResult> ExchangeCodeAsync(string code, CancellationToken ct = default) =>
            Task.FromResult(new TokenResult
            {
                AccessToken = "at",
                RefreshToken = RefreshToken,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
                UserPrincipalName = Upn,
                Subject = Subject,
                Email = Upn,
                DisplayName = DisplayName,
            });

        public Task<TokenResult> ExchangeIdentityCodeAsync(string code, CancellationToken ct = default) =>
            ExchangeCodeAsync(code, ct);

        public Task<TokenResult> ExchangeCalendarCodeAsync(
            string code, string scopes, CancellationToken ct = default) =>
            ExchangeCodeAsync(code, ct);

        public Task<TokenResult> RefreshAsync(string refreshToken, CancellationToken ct = default) =>
            Task.FromResult(new TokenResult
            {
                AccessToken = "at",
                RefreshToken = refreshToken,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
            });
    }

    private sealed class RecordingWriter : ICalendarWriter
    {
        private readonly List<string?> _resolvedAccountRefs;
        public RecordingWriter(List<string?> resolvedAccountRefs) => _resolvedAccountRefs = resolvedAccountRefs;

        public Task<IReadOnlyList<CalendarOption>> ListCalendarsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CalendarOption>>(Array.Empty<CalendarOption>());

        public Task<CalendarOption> CreateCalendarAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(new CalendarOption { Id = "new", DisplayName = name });

        public Task<MirrorResult> MirrorAsync(
            string calendarId, IReadOnlyList<AppointmentRecord> records, int reminderMinutes,
            DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default, string pairId = "") =>
            Task.FromResult(new MirrorResult { Created = records.Count });
    }

    private sealed class RecordingReader : ICalendarReader
    {
        public Task<IReadOnlyList<AppointmentRecord>> ReadWindowAsync(
            string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc,
            CancellationToken ct = default, bool preserveLocalTime = false) =>
            Task.FromResult<IReadOnlyList<AppointmentRecord>>(new[] { MakeEvent("r1") });
    }

    // Captures the account ref the SyncService resolves for the /api/sync/calendar path.
    private sealed class RecordingTarget : ICalendarTarget
    {
        public Task<IReadOnlyList<CalendarTargetInfo>> ListCalendarsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CalendarTargetInfo>>(new[]
            {
                new CalendarTargetInfo { Id = "cal-default", DisplayName = "Calendar", IsDefault = true },
            });

        public Task<CalendarTargetInfo> CreateCalendarAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(new CalendarTargetInfo { Id = "n", DisplayName = name });

        public Task<IReadOnlyDictionary<string, ExistingEventLookup>> FindByExternalIdsAsync(
            string calendarId, IReadOnlyList<string> externalIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, ExistingEventLookup>>(
                new Dictionary<string, ExistingEventLookup>(StringComparer.Ordinal));

        public Task<string> CreateEventAsync(string calendarId, EventDraft draft, CancellationToken ct = default) =>
            Task.FromResult("evt");

        public Task UpdateEventAsync(string eventId, EventDraft draft, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteEventAsync(string eventId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ManagedEventRef>> ListManagedInWindowAsync(
            string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ManagedEventRef>>(Array.Empty<ManagedEventRef>());
    }

    // Per-test harness: the real EF stores plus recording providers. The account refs that
    // ResolveWriter/ResolveReader (pairs) and the sync-target factory (/api/sync) see are
    // captured into separate lists so tests can assert which account a run/push/sync used.
    private sealed class Harness : IDisposable
    {
        public ServerTestFactory Inner { get; }
        public WebApplicationFactory<Program> Factory { get; }
        public SwitchableIdentityService Identity { get; } = new();
        public List<string?> PairWriterAccountRefs { get; } = new();
        public List<string?> PairReaderAccountRefs { get; } = new();
        public List<string> SyncTargetUpns { get; } = new();

        public Harness()
        {
            Inner = new ServerTestFactory();
            var pairWriterRefs = PairWriterAccountRefs;
            var pairReaderRefs = PairReaderAccountRefs;
            var syncUpns = SyncTargetUpns;
            Factory = Inner.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMicrosoftTokenService>();
                services.AddSingleton<IMicrosoftTokenService>(Identity);

                services.RemoveAll<ProviderRegistry>();
                services.AddSingleton(new ProviderRegistry(
                    readerFactory: accountRef => { pairReaderRefs.Add(accountRef); return new RecordingReader(); },
                    writerFactory: accountRef => { pairWriterRefs.Add(accountRef); return new RecordingWriter(pairWriterRefs); }));

                services.RemoveAll<Func<string, ICalendarTarget>>();
                services.AddSingleton<Func<string, ICalendarTarget>>(_ => upn =>
                {
                    syncUpns.Add(upn);
                    return new RecordingTarget();
                });
            }));
        }

        // Signs in as the given identity via the real OAuth flow; returns a cookie client.
        public async Task<HttpClient> SignInAsync(string subject, string upn, string display, string rt)
        {
            Identity.Use(subject, upn, display, rt);
            return await CookieAuthHelper.SignInAsync(Factory);
        }

        // The ZyncMaster user id for a given identity subject (idempotent upsert returns it).
        public async Task<string> UserIdForAsync(string subject, string upn, string display)
        {
            var users = Factory.Services.GetRequiredService<IUserStore>();
            var row = await users.UpsertAsync("microsoft", subject, upn, display);
            return row.Id;
        }

        // Inserts a device bound to an explicit user id and returns its plaintext api key.
        // EfDeviceStore.AddAsync honors a non-default explicit UserId on the domain device.
        public async Task<string> AddDeviceForUserAsync(string userId, string name = "Laptop")
        {
            var devices = Factory.Services.GetRequiredService<IDeviceStore>();
            var key = ApiKeyGenerator.Generate();
            await devices.AddAsync(new Device
            {
                Id = Guid.NewGuid().ToString("N"),
                UserId = userId,
                Name = name,
                ApiKeyHash = ApiKeyHasher.Hash(key),
                CreatedUtc = DateTimeOffset.UtcNow,
            });
            return key;
        }

        public HttpClient DeviceClient(string apiKey)
        {
            var client = Factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
            return client;
        }

        public void Dispose()
        {
            Factory.Dispose();
            Inner.Dispose();
        }
    }

    private static AppointmentRecord MakeEvent(string id)
    {
        var start = DateTimeOffset.UtcNow.AddDays(1);
        return new AppointmentRecord
        {
            Id = id,
            Subject = id,
            StartOffset = start,
            EndOffset = start.AddHours(1),
            StartTimeZoneId = "UTC",
        };
    }

    private static object PairBody(
        string name = "Pair",
        string destAccountRef = "alice@test",
        string sourceProvider = "OutlookCom") => new
    {
        name,
        // OutlookCom sources have no server reader (their events arrive via /push); a
        // MicrosoftGraph source has one, so /run can read+mirror end to end. The source
        // account ref mirrors the destination so a Graph source resolves the caller's account.
        source = sourceProvider == "MicrosoftGraph"
            ? (object)new { provider = "MicrosoftGraph", accountRef = destAccountRef, calendarId = "src", calendarName = "Src" }
            : new { provider = "OutlookCom", calendarId = "src", calendarName = "Src" },
        destination = new { provider = "MicrosoftGraph", accountRef = destAccountRef, calendarId = "dst", calendarName = "Dst" },
        intervalMin = 15,
    };

    private static async Task<string> CreatePairAsync(HttpClient client, object body)
    {
        var resp = await client.PostAsJsonAsync("/api/pairs", body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    // ---- Pair isolation across the panel (cookie) surface ----------------------------

    [Fact]
    public async Task UserB_does_not_see_UserA_pairs_and_gets_404_on_As_pair_id()
    {
        using var h = new Harness();
        var aClient = await h.SignInAsync("oid-a", "alice@test", "Alice", "rt-a");
        var aPairId = await CreatePairAsync(aClient, PairBody("A pair", "alice@test"));

        var bClient = await h.SignInAsync("oid-b", "bob@test", "Bob", "rt-b");

        // B's listing is empty.
        var bList = await bClient.GetFromJsonAsync<JsonElement>("/api/pairs");
        bList.GetArrayLength().Should().Be(0);

        // B cannot read, patch or delete A's pair: 404, not 403, not 500.
        (await bClient.GetAsync($"/api/pairs/{aPairId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await bClient.PatchAsJsonAsync($"/api/pairs/{aPairId}", new { name = "Hijack" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await bClient.DeleteAsync($"/api/pairs/{aPairId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // A still sees and owns the pair after B's attempts.
        var aList = await aClient.GetFromJsonAsync<JsonElement>("/api/pairs");
        aList.GetArrayLength().Should().Be(1);
        (await aClient.GetAsync($"/api/pairs/{aPairId}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UserA_device_cannot_push_or_run_UserB_pair()
    {
        using var h = new Harness();

        // B owns a pair.
        var bClient = await h.SignInAsync("oid-b", "bob@test", "Bob", "rt-b");
        var bPairId = await CreatePairAsync(bClient, PairBody("B pair", "bob@test"));

        // A signs in, connects an account, and registers a device bound to A.
        await h.SignInAsync("oid-a", "alice@test", "Alice", "rt-a");
        var aUserId = await h.UserIdForAsync("oid-a", "alice@test", "Alice");
        var aKey = await h.AddDeviceForUserAsync(aUserId);
        var aDevice = h.DeviceClient(aKey);

        // A's device addressing B's pair id resolves to null under A's scope -> 404.
        (await aDevice.PostAsJsonAsync($"/api/pairs/{bPairId}/push", new { events = Array.Empty<AppointmentRecord>() }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await aDevice.PostAsync($"/api/pairs/{bPairId}/run", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        // No mirror happened for either side.
        h.PairWriterAccountRefs.Should().BeEmpty();
        h.PairReaderAccountRefs.Should().BeEmpty();
    }

    [Fact]
    public async Task UserA_device_push_mirrors_using_UserAs_account_ref()
    {
        using var h = new Harness();

        // A signs in (connects alice@test) and owns a pair whose destination is A's account.
        var aClient = await h.SignInAsync("oid-a", "alice@test", "Alice", "rt-a");
        var aPairId = await CreatePairAsync(aClient, PairBody("A pair", "alice@test"));
        var aUserId = await h.UserIdForAsync("oid-a", "alice@test", "Alice");
        var aDevice = h.DeviceClient(await h.AddDeviceForUserAsync(aUserId));

        var resp = await aDevice.PostAsJsonAsync($"/api/pairs/{aPairId}/push", new
        {
            events = new[] { MakeEvent("e1"), MakeEvent("e2") },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        // The writer was resolved with A's account ref — not "default", not "bob@test".
        h.PairWriterAccountRefs.Should().ContainSingle().Which.Should().Be("alice@test");
    }

    // ---- /api/pairs/{id}/run accepts EITHER scheme (cookie panel OR device api key) -----

    [Fact]
    public async Task PanelCookie_run_on_own_pair_returns_200_and_mirrors()
    {
        using var h = new Harness();

        // A signs in via cookie, connects alice@test, and owns a Graph-sourced pair (so /run
        // has a server reader). The "Sync now" button in the browser panel posts /run under
        // the cookie; it must succeed (200) rather than 401-and-drop-to-the-sign-in-gate.
        var aClient = await h.SignInAsync("oid-a", "alice@test", "Alice", "rt-a");
        var aPairId = await CreatePairAsync(aClient, PairBody("A pair", "alice@test", sourceProvider: "MicrosoftGraph"));

        var resp = await aClient.PostAsync($"/api/pairs/{aPairId}/run", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        // The reader/writer resolved with A's account — proving the cookie identity flowed.
        h.PairReaderAccountRefs.Should().ContainSingle().Which.Should().Be("alice@test");
        h.PairWriterAccountRefs.Should().ContainSingle().Which.Should().Be("alice@test");
    }

    [Fact]
    public async Task DeviceApiKey_run_on_own_pair_still_returns_200()
    {
        using var h = new Harness();

        var aClient = await h.SignInAsync("oid-a", "alice@test", "Alice", "rt-a");
        var aPairId = await CreatePairAsync(aClient, PairBody("A pair", "alice@test", sourceProvider: "MicrosoftGraph"));
        var aUserId = await h.UserIdForAsync("oid-a", "alice@test", "Alice");
        var aDevice = h.DeviceClient(await h.AddDeviceForUserAsync(aUserId));

        var resp = await aDevice.PostAsync($"/api/pairs/{aPairId}/run", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        h.PairWriterAccountRefs.Should().ContainSingle().Which.Should().Be("alice@test");
    }

    [Fact]
    public async Task Run_with_no_credentials_returns_401()
    {
        using var h = new Harness();

        var aClient = await h.SignInAsync("oid-a", "alice@test", "Alice", "rt-a");
        var aPairId = await CreatePairAsync(aClient, PairBody("A pair", "alice@test", sourceProvider: "MicrosoftGraph"));

        var anon = h.Factory.CreateClient();
        var resp = await anon.PostAsync($"/api/pairs/{aPairId}/run", null);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PanelCookie_run_on_another_users_pair_returns_404()
    {
        using var h = new Harness();

        // A owns a Graph-sourced pair.
        var aClient = await h.SignInAsync("oid-a", "alice@test", "Alice", "rt-a");
        var aPairId = await CreatePairAsync(aClient, PairBody("A pair", "alice@test", sourceProvider: "MicrosoftGraph"));

        // B signs in (cookie) and posts /run against A's pair id -> user-scoped store
        // resolves null -> 404, never 200 and never a leak. No mirror happens.
        var bClient = await h.SignInAsync("oid-b", "bob@test", "Bob", "rt-b");
        var resp = await bClient.PostAsync($"/api/pairs/{aPairId}/run", null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        h.PairReaderAccountRefs.Should().BeEmpty();
        h.PairWriterAccountRefs.Should().BeEmpty();
    }

    // ---- export-source-txt is user-scoped: B cannot export A's source via Graph --------

    [Fact]
    public async Task UserB_cannot_export_source_txt_of_UserAs_pair()
    {
        using var h = new Harness();

        // A owns a Graph-sourced pair (so it HAS a server reader; without scoping B could read it).
        var aClient = await h.SignInAsync("oid-a", "alice@test", "Alice", "rt-a");
        var aPairId = await CreatePairAsync(aClient, PairBody("A pair", "alice@test", sourceProvider: "MicrosoftGraph"));

        // B signs in (cookie) and posts export-source-txt against A's pair id. The user-scoped store
        // resolves null -> 404, so the endpoint never resolves a reader and never touches A's source
        // calendar via Graph.
        var bClient = await h.SignInAsync("oid-b", "bob@test", "Bob", "rt-b");
        var bResp = await bClient.PostAsJsonAsync($"/api/pairs/{aPairId}/export-source-txt",
            new { year = 2026, month = 6, includeCancelled = true });

        bResp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        h.PairReaderAccountRefs.Should().BeEmpty();

        // A's own device key also cannot export A's pair via this human-only surface (it requires a
        // cookie or identity bearer), AND A's owner can still export it under the cookie.
        var aResp = await aClient.PostAsJsonAsync($"/api/pairs/{aPairId}/export-source-txt",
            new { year = 2026, month = 6, includeCancelled = true });
        aResp.StatusCode.Should().Be(HttpStatusCode.OK);
        // A's export resolved the reader with A's account ref — proving scoping let the OWNER through.
        h.PairReaderAccountRefs.Should().ContainSingle().Which.Should().Be("alice@test");
    }

    // ---- destination cleanup is user-scoped: B cannot clean A's pair -------------------

    [Fact]
    public async Task UserB_cannot_cleanup_or_count_destination_of_UserAs_pair()
    {
        using var h = new Harness();

        var aClient = await h.SignInAsync("oid-a", "alice@test", "Alice", "rt-a");
        var aPairId = await CreatePairAsync(aClient, PairBody("A pair", "alice@test", sourceProvider: "MicrosoftGraph"));

        var bClient = await h.SignInAsync("oid-b", "bob@test", "Bob", "rt-b");
        var oldDestination = new { provider = "MicrosoftGraph", accountRef = "bob@test", calendarId = "old-cal", calendarName = "Old" };

        // B posting cleanup against A's pair id -> user-scoped store resolves null -> 404. The writer
        // factory is never invoked (no destructive enumeration of any calendar).
        var cleanup = await bClient.PostAsJsonAsync($"/api/pairs/{aPairId}/cleanup-destination",
            new { destination = oldDestination });
        cleanup.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var count = await bClient.GetAsync(
            $"/api/pairs/{aPairId}/managed-count?provider=MicrosoftGraph&accountRef=bob@test&calendarId=old-cal");
        count.StatusCode.Should().Be(HttpStatusCode.NotFound);

        h.PairWriterAccountRefs.Should().BeEmpty();
    }

    // ---- Device sync path (/api/sync/calendar) uses the device owner's account ---------

    [Fact]
    public async Task Sync_calendar_uses_device_owners_connected_account_ref()
    {
        using var h = new Harness();

        // Both A and B sign in and connect their own accounts under their real UPNs.
        await h.SignInAsync("oid-a", "alice@test", "Alice", "rt-a");
        await h.SignInAsync("oid-b", "bob@test", "Bob", "rt-b");

        var aUserId = await h.UserIdForAsync("oid-a", "alice@test", "Alice");
        var aDevice = h.DeviceClient(await h.AddDeviceForUserAsync(aUserId));

        var resp = await aDevice.PostAsJsonAsync("/api/sync/calendar", new SyncRequest
        {
            Events = new List<AppointmentRecord> { MakeEvent("s1") },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        // The sync target factory was called with A's account ref — proving the device
        // owner's connected account drove the mirror, not a global "default" or B's account.
        h.SyncTargetUpns.Should().ContainSingle().Which.Should().Be("alice@test");
    }

    [Fact]
    public async Task Sync_calendar_for_owner_without_connected_account_returns_409()
    {
        using var h = new Harness();

        // B has an account, but A has none. A's device must get a per-user 409.
        await h.SignInAsync("oid-b", "bob@test", "Bob", "rt-b");

        // Create user A without connecting an account, then bind a device to A.
        var aUserId = await h.UserIdForAsync("oid-a", "alice@test", "Alice");
        var aDevice = h.DeviceClient(await h.AddDeviceForUserAsync(aUserId));

        var resp = await aDevice.PostAsJsonAsync("/api/sync/calendar", new SyncRequest
        {
            Events = new List<AppointmentRecord> { MakeEvent("s1") },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetString().Should().Be("no_connected_account");
        h.SyncTargetUpns.Should().BeEmpty();
    }

    // ---- Accounts + devices listing scoping -------------------------------------------

    [Fact]
    public async Task Accounts_and_devices_listings_return_only_callers_rows()
    {
        using var h = new Harness();

        var aClient = await h.SignInAsync("oid-a", "alice@test", "Alice", "rt-a");
        var bClient = await h.SignInAsync("oid-b", "bob@test", "Bob", "rt-b");

        var aUserId = await h.UserIdForAsync("oid-a", "alice@test", "Alice");
        var bUserId = await h.UserIdForAsync("oid-b", "bob@test", "Bob");
        var aKey = await h.AddDeviceForUserAsync(aUserId, "A-Laptop");
        var bKey = await h.AddDeviceForUserAsync(bUserId, "B-Phone");

        // /api/accounts (cookie): each user sees only their own connected account.
        var aAccounts = await aClient.GetFromJsonAsync<JsonElement>("/api/accounts");
        aAccounts.GetArrayLength().Should().Be(1);
        aAccounts[0].GetProperty("accountRef").GetString().Should().Be("alice@test");

        var bAccounts = await bClient.GetFromJsonAsync<JsonElement>("/api/accounts");
        bAccounts.GetArrayLength().Should().Be(1);
        bAccounts[0].GetProperty("accountRef").GetString().Should().Be("bob@test");

        // /api/devices (api key -> device owner): each device only lists its owner's devices.
        var aDevices = await h.DeviceClient(aKey).GetFromJsonAsync<JsonElement>("/api/devices");
        aDevices.EnumerateArray().Select(d => d.GetProperty("name").GetString())
            .Should().BeEquivalentTo(new[] { "A-Laptop" });

        var bDevices = await h.DeviceClient(bKey).GetFromJsonAsync<JsonElement>("/api/devices");
        bDevices.EnumerateArray().Select(d => d.GetProperty("name").GetString())
            .Should().BeEquivalentTo(new[] { "B-Phone" });
    }

    [Fact]
    public async Task UserB_cannot_read_calendars_or_unlink_UserAs_account()
    {
        using var h = new Harness();

        await h.SignInAsync("oid-a", "alice@test", "Alice", "rt-a");
        var bClient = await h.SignInAsync("oid-b", "bob@test", "Bob", "rt-b");

        // B addressing A's account ref: not owned -> 404 (no existence leak, no Graph 500).
        (await bClient.GetAsync("/api/accounts/alice@test/calendars"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await bClient.DeleteAsync("/api/accounts/alice@test"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        // A's account survived B's unlink attempt.
        var aClient2 = await h.SignInAsync("oid-a", "alice@test", "Alice", "rt-a");
        var aAccounts = await aClient2.GetFromJsonAsync<JsonElement>("/api/accounts");
        aAccounts.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Unlink_only_affects_callers_account_and_pairs()
    {
        using var h = new Harness();

        // B owns an account + a pair referencing it.
        var bClient = await h.SignInAsync("oid-b", "bob@test", "Bob", "rt-b");
        var bPairId = await CreatePairAsync(bClient, PairBody("B pair", "bob@test"));

        // A owns an account + a pair referencing it.
        var aClient = await h.SignInAsync("oid-a", "alice@test", "Alice", "rt-a");
        var aPairId = await CreatePairAsync(aClient, PairBody("A pair", "alice@test"));

        // A unlinks A's own account.
        var del = await aClient.DeleteAsync("/api/accounts/alice@test");
        del.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await del.Content.ReadAsStringAsync());
        var affected = doc.RootElement.GetProperty("affectedPairIds")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        affected.Should().BeEquivalentTo(new[] { aPairId });

        // A's account is gone; B's account and pair are untouched.
        var aAccounts = await aClient.GetFromJsonAsync<JsonElement>("/api/accounts");
        aAccounts.GetArrayLength().Should().Be(0);

        var bAccounts = await bClient.GetFromJsonAsync<JsonElement>("/api/accounts");
        bAccounts.GetArrayLength().Should().Be(1);

        var bPair = await bClient.GetFromJsonAsync<JsonElement>($"/api/pairs/{bPairId}");
        bPair.GetProperty("state").GetString().Should().Be("active");
    }
}
