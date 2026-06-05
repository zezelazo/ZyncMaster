using System;
using System.Collections.Generic;
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

// Covers the §F2 destination-cleanup endpoints:
//   POST /api/pairs/{id}/cleanup-destination  (delete this pair's managed events from an OLD destination)
//   GET  /api/pairs/{id}/managed-count        (count them for the wizard confirm)
// Every Graph boundary is a recording fake; no network, no Outlook.
public class PairDestinationCleanupTests : IClassFixture<ServerTestFactory>
{
    private readonly ServerTestFactory _factory;

    public PairDestinationCleanupTests(ServerTestFactory factory) => _factory = factory;

    // Records cleanup/count calls and returns canned results. It implements the cleanup/count
    // members directly (not the interface defaults) so the test can assert the calendarId+pairId
    // it was asked to clean and prove the endpoint never cleans the wrong calendar.
    private sealed class RecordingWriter : ICalendarWriter
    {
        public List<(string calendarId, string pairId)> CleanupCalls { get; } = new();
        public List<(string calendarId, string pairId)> CountCalls { get; } = new();
        public CleanupResult CleanupToReturn { get; set; } = new() { Deleted = 0 };
        public int CountToReturn { get; set; }

        public Task<IReadOnlyList<CalendarOption>> ListCalendarsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CalendarOption>>(Array.Empty<CalendarOption>());
        public Task<CalendarOption> CreateCalendarAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(new CalendarOption { Id = "n", DisplayName = name });
        public Task<MirrorResult> MirrorAsync(
            string calendarId, IReadOnlyList<AppointmentRecord> records, int reminderMinutes,
            DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default, string pairId = "") =>
            Task.FromResult(new MirrorResult());

        public Task<CleanupResult> CleanupManagedAsync(string calendarId, string pairId, CancellationToken ct = default)
        {
            CleanupCalls.Add((calendarId, pairId));
            return Task.FromResult(CleanupToReturn);
        }

        public Task<int> CountManagedAsync(string calendarId, string pairId, CancellationToken ct = default)
        {
            CountCalls.Add((calendarId, pairId));
            return Task.FromResult(CountToReturn);
        }
    }

    private sealed class FakeReader : ICalendarReader
    {
        public Task<IReadOnlyList<AppointmentRecord>> ReadWindowAsync(
            string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc,
            CancellationToken ct = default, bool preserveLocalTime = false) =>
            Task.FromResult<IReadOnlyList<AppointmentRecord>>(Array.Empty<AppointmentRecord>());
    }

    private WebApplicationFactory<Program> Build(RecordingWriter writer)
        => Build(writer, adapter: null);

    private WebApplicationFactory<Program> Build(RecordingWriter writer, ILegacyConnectedAccountAdapter? adapter)
        => _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMicrosoftTokenService>();
                services.AddSingleton<IMicrosoftTokenService>(
                    new CookieAuthHelper.FakeIdentityTokenService { Upn = "" });

                services.RemoveAll<ProviderRegistry>();
                services.AddSingleton(new ProviderRegistry(
                    _ => new FakeReader(),
                    _ => writer));

                services.RemoveAll<IConnectedAccountStore>();
                services.AddSingleton<IConnectedAccountStore>(_ =>
                {
                    var store = new DataProtectionConnectedAccountStore(DataProtectionProvider.Create("tests"));
                    store.SetAsync("default", "rt").GetAwaiter().GetResult();
                    return store;
                });

                services.RemoveAll<ISyncPairStore>();
                services.AddSingleton<ISyncPairStore, InMemorySyncPairStore>();

                // Optional adapter override: lets a test pin cross-representation account resolution
                // (legacy UPN vs pool accountId for the SAME mailbox) so the canonical
                // destination_is_current guard can be exercised end-to-end.
                if (adapter is not null)
                {
                    services.RemoveAll<ILegacyConnectedAccountAdapter>();
                    services.AddSingleton(adapter);
                }
            });
        });

    // Fake adapter that maps a set of accountRefs to canonical accountIds + mailbox emails, so the
    // cleanup endpoint sees the SAME mailbox referenced two different ways collapse onto one account.
    private sealed class FakeAdapter : ILegacyConnectedAccountAdapter
    {
        private readonly Dictionary<string, string> _refToId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _idToEmail = new(StringComparer.Ordinal);

        public void Map(string accountRef, string accountId, string email)
        {
            _refToId[accountRef] = accountId;
            _idToEmail[accountId] = email;
        }

        public string DeriveAccountId(string userId, string? accountRef) => accountRef ?? "";

        public Task<string> ResolveAccountIdAsync(string? accountRef, CancellationToken ct = default) =>
            Task.FromResult(_refToId.TryGetValue(accountRef ?? "", out var id) ? id : (accountRef ?? ""));

        public Task<CalendarAccount?> ResolveAsync(string accountId, CancellationToken ct = default)
        {
            if (!_idToEmail.TryGetValue(accountId, out var email))
                return Task.FromResult<CalendarAccount?>(null);
            return Task.FromResult<CalendarAccount?>(new CalendarAccount
            {
                Id = accountId,
                UserId = "u",
                Kind = AccountKind.Graph,
                Provider = "microsoft",
                AccountEmail = email,
                Scope = AccountScope.ReadWrite,
                ConnectedAt = DateTimeOffset.UnixEpoch,
            });
        }

        public Task<string?> ResolveRefreshTokenAsync(string accountId, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task UpdateRefreshTokenAsync(string accountId, string refreshToken, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private static Task<HttpClient> AuthedClientAsync(WebApplicationFactory<Program> factory) =>
        CookieAuthHelper.SignInAsync(factory);

    // Pair whose CURRENT destination is "cur-cal"; the OLD destination to clean is "old-cal".
    private static object GraphPairBody() => new
    {
        name = "Graph pair",
        source = new { provider = "MicrosoftGraph", accountRef = "default", calendarId = "srcCal", calendarName = "Src" },
        destination = new { provider = "MicrosoftGraph", accountRef = "default", calendarId = "cur-cal", calendarName = "Current" },
        intervalMin = 15,
    };

    private static object OldDestination(string calendarId = "old-cal") => new
    {
        provider = "MicrosoftGraph",
        accountRef = "default",
        calendarId,
        calendarName = "Old",
    };

    private static async Task<string> CreatePairAsync(HttpClient client, object body)
    {
        var create = await client.PostAsJsonAsync("/api/pairs", body);
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Cleanup_requires_cookie()
    {
        var writer = new RecordingWriter();
        var factory = Build(writer);
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/pairs/anything/cleanup-destination",
            new { destination = OldDestination() });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        writer.CleanupCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Cleanup_unknown_pair_returns_404_and_deletes_nothing()
    {
        var writer = new RecordingWriter();
        var factory = Build(writer);
        var client = await AuthedClientAsync(factory);

        var resp = await client.PostAsJsonAsync("/api/pairs/missing/cleanup-destination",
            new { destination = OldDestination() });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        writer.CleanupCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Cleanup_deletes_only_this_pairs_managed_events_in_the_old_destination()
    {
        var writer = new RecordingWriter { CleanupToReturn = new CleanupResult { Deleted = 3 } };
        var factory = Build(writer);
        var client = await AuthedClientAsync(factory);
        var id = await CreatePairAsync(client, GraphPairBody());

        var resp = await client.PostAsJsonAsync($"/api/pairs/{id}/cleanup-destination",
            new { destination = OldDestination("old-cal") });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("deleted").GetInt32().Should().Be(3);

        // The cleanup ran against the OLD calendar and was scoped to THIS pair's id — never the
        // whole calendar, never another calendar.
        writer.CleanupCalls.Should().ContainSingle();
        writer.CleanupCalls[0].calendarId.Should().Be("old-cal");
        writer.CleanupCalls[0].pairId.Should().Be(id);
    }

    [Fact]
    public async Task Cleanup_refuses_the_pairs_current_destination()
    {
        var writer = new RecordingWriter();
        var factory = Build(writer);
        var client = await AuthedClientAsync(factory);
        var id = await CreatePairAsync(client, GraphPairBody());

        // Asking to clean the CURRENT destination ("cur-cal") must be rejected — it would delete the
        // events the latest sync just wrote.
        var resp = await client.PostAsJsonAsync($"/api/pairs/{id}/cleanup-destination",
            new { destination = OldDestination("cur-cal") });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("destination_is_current");
        writer.CleanupCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Cleanup_refuses_current_destination_referenced_with_alternate_account_representation()
    {
        // The pair's CURRENT destination is "default"/cur-cal. The SAME mailbox is also reachable as a
        // pool account "pool-alt" (distinct accountId) — a raw Ordinal AccountRef compare would treat
        // it as a DIFFERENT destination and wrongly clean the current one. The canonical guard must
        // collapse the two representations (by mailbox) and reject with destination_is_current.
        var adapter = new FakeAdapter();
        adapter.Map("default", "acct-legacy", "user@x");   // pair's current destination ref
        adapter.Map("pool-alt", "acct-pool", "USER@x");     // same mailbox, different accountId

        var writer = new RecordingWriter();
        var factory = Build(writer, adapter);
        var client = await AuthedClientAsync(factory);
        var id = await CreatePairAsync(client, GraphPairBody());

        var resp = await client.PostAsJsonAsync($"/api/pairs/{id}/cleanup-destination",
            new { destination = new { provider = "MicrosoftGraph", accountRef = "pool-alt", calendarId = "cur-cal", calendarName = "Current (pool)" } });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("destination_is_current");
        writer.CleanupCalls.Should().BeEmpty("the canonical guard must stop the destructive cleanup before any writer call");
    }

    [Fact]
    public async Task Cleanup_unknown_destination_account_returns_400()
    {
        var writer = new RecordingWriter();
        var factory = Build(writer);
        var client = await AuthedClientAsync(factory);
        var id = await CreatePairAsync(client, GraphPairBody());

        var resp = await client.PostAsJsonAsync($"/api/pairs/{id}/cleanup-destination",
            new { destination = new { provider = "MicrosoftGraph", accountRef = "ghost@test", calendarId = "old-cal", calendarName = "Old" } });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("destination.accountRef");
        writer.CleanupCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Cleanup_outlookcom_destination_is_noop_and_deletes_nothing()
    {
        var writer = new RecordingWriter();
        var factory = Build(writer);
        var client = await AuthedClientAsync(factory);
        var id = await CreatePairAsync(client, GraphPairBody());

        // An OutlookCom "old destination" has no server-side managed events; report deleted=0
        // without ever resolving a writer or deleting anything.
        var resp = await client.PostAsJsonAsync($"/api/pairs/{id}/cleanup-destination",
            new { destination = new { provider = "OutlookCom", calendarId = "local", calendarName = "Outlook (this PC)" } });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("deleted").GetInt32().Should().Be(0);
        writer.CleanupCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Cleanup_missing_destination_returns_validation_400()
    {
        var writer = new RecordingWriter();
        var factory = Build(writer);
        var client = await AuthedClientAsync(factory);
        var id = await CreatePairAsync(client, GraphPairBody());

        var resp = await client.PostAsJsonAsync($"/api/pairs/{id}/cleanup-destination", new { });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        writer.CleanupCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task ManagedCount_returns_count_for_this_pair_in_the_destination()
    {
        var writer = new RecordingWriter { CountToReturn = 5 };
        var factory = Build(writer);
        var client = await AuthedClientAsync(factory);
        var id = await CreatePairAsync(client, GraphPairBody());

        var resp = await client.GetAsync(
            $"/api/pairs/{id}/managed-count?provider=MicrosoftGraph&accountRef=default&calendarId=old-cal");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(5);

        writer.CountCalls.Should().ContainSingle();
        writer.CountCalls[0].calendarId.Should().Be("old-cal");
        writer.CountCalls[0].pairId.Should().Be(id);
    }

    [Fact]
    public async Task ManagedCount_unknown_pair_returns_404()
    {
        var writer = new RecordingWriter();
        var factory = Build(writer);
        var client = await AuthedClientAsync(factory);

        var resp = await client.GetAsync(
            "/api/pairs/missing/managed-count?provider=MicrosoftGraph&accountRef=default&calendarId=old-cal");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        writer.CountCalls.Should().BeEmpty();
    }

    // ── FIX 3 — eventual server-side drain of a re-targeted pair's old destination ────────────

    private static async Task PatchPairAsync(HttpClient client, string id, object body)
    {
        var resp = await client.PatchAsync($"/api/pairs/{id}",
            JsonContent.Create(body));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Retarget_then_run_without_client_cleanup_drains_the_old_destination_for_this_pair_only()
    {
        var writer = new RecordingWriter();
        var factory = Build(writer);
        var client = await AuthedClientAsync(factory);

        // Pair's current destination is "cur-cal".
        var id = await CreatePairAsync(client, GraphPairBody());

        // Re-target the destination to "new-cal" WITHOUT ever calling /cleanup-destination
        // (simulating a client crash/close right after the edit). The old "cur-cal" still holds the
        // events this pair created.
        await PatchPairAsync(client, id, new
        {
            destination = new { provider = "MicrosoftGraph", accountRef = "default", calendarId = "new-cal", calendarName = "New" },
        });

        writer.CleanupCalls.Should().BeEmpty("the PATCH only queues the cleanup; it does not delete");

        // The next run drains the queued old destination idempotently, server-side, before mirroring.
        var run = await client.PostAsync($"/api/pairs/{id}/run", content: null);
        run.StatusCode.Should().Be(HttpStatusCode.OK);

        writer.CleanupCalls.Should().ContainSingle("the old destination is drained on the next run");
        writer.CleanupCalls[0].calendarId.Should().Be("cur-cal", "only the OLD destination is cleaned");
        writer.CleanupCalls[0].pairId.Should().Be(id, "the drain is scoped to THIS pair only");
    }

    [Fact]
    public async Task Drained_old_destination_is_not_cleaned_again_on_a_subsequent_run()
    {
        var writer = new RecordingWriter();
        var factory = Build(writer);
        var client = await AuthedClientAsync(factory);
        var id = await CreatePairAsync(client, GraphPairBody());

        await PatchPairAsync(client, id, new
        {
            destination = new { provider = "MicrosoftGraph", accountRef = "default", calendarId = "new-cal", calendarName = "New" },
        });

        (await client.PostAsync($"/api/pairs/{id}/run", content: null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync($"/api/pairs/{id}/run", content: null)).StatusCode.Should().Be(HttpStatusCode.OK);

        // The first run drained it (no failures), so the entry is dequeued and the second run does
        // NOT re-clean it.
        writer.CleanupCalls.Should().ContainSingle("a fully drained entry must not be cleaned again");
    }

    [Fact]
    public async Task ManagedCount_outlookcom_destination_returns_zero_without_resolving_writer()
    {
        var writer = new RecordingWriter { CountToReturn = 99 };
        var factory = Build(writer);
        var client = await AuthedClientAsync(factory);
        var id = await CreatePairAsync(client, GraphPairBody());

        var resp = await client.GetAsync(
            $"/api/pairs/{id}/managed-count?provider=OutlookCom&calendarId=local");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("count").GetInt32().Should().Be(0);
        writer.CountCalls.Should().BeEmpty();
    }
}
