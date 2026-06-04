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
using ZyncMaster.Graph;
using ZyncMaster.Server.Data;
using Xunit;

namespace ZyncMaster.Server.Tests.Sync;

// feat/unify-account-api — the pairs-management read/validation surface (/api/accounts,
// /api/accounts/{ref}/calendars, POST /api/pairs) must resolve accounts through the adapter
// (pool-first + legacy fallback), so a calendar account connected by the NEW per-user pool flow
// (ICalendarAccountStore) is listed, its calendars are enumerable, and it is accepted when
// creating a pair — exactly like a legacy account. Cross-user isolation must be preserved.
//
// All endpoints are cookie-gated; the App reaches them under the panel session cookie (NOT the
// device api key — that surface is asserted rejected elsewhere). The pool and legacy stores are
// user-scoped via ICurrentUserAccessor, which under the cookie resolves the signed-in user's id,
// so the user's pool is read correctly with no auth changes.
public class UnifyAccountApiTests
{
    // The identity the cookie sign-in flow mints (CookieAuthHelper default subject/upn).
    private const string CookieSubject = "oid-123";
    private const string CookieUpn = "user@test";
    private const string CookieDisplay = "Test User";

    private sealed class FakeTarget : ICalendarTarget
    {
        public IReadOnlyList<CalendarTargetInfo> Calendars { get; set; } = new[]
        {
            new CalendarTargetInfo { Id = "poolcal1", DisplayName = "Pool Primary", IsDefault = true, Owner = "pool@test" },
        };
        public Task<IReadOnlyList<CalendarTargetInfo>> ListCalendarsAsync(CancellationToken ct = default) =>
            Task.FromResult(Calendars);
        public Task<CalendarTargetInfo> CreateCalendarAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(new CalendarTargetInfo { Id = "n", DisplayName = name });
        public Task<IReadOnlyDictionary<string, ExistingEventLookup>> FindByExternalIdsAsync(
            string calendarId, IReadOnlyList<string> externalIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, ExistingEventLookup>>(new Dictionary<string, ExistingEventLookup>());
        public Task<string> CreateEventAsync(string calendarId, EventDraft draft, CancellationToken ct = default) =>
            Task.FromResult("id");
        public Task UpdateEventAsync(string eventId, EventDraft draft, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteEventAsync(string eventId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ManagedEventRef>> ListManagedInWindowAsync(
            string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ManagedEventRef>>(Array.Empty<ManagedEventRef>());
    }

    private sealed class StubTokenProvider : IGraphTokenProvider
    {
        public Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default) =>
            Task.FromResult("token");
    }

    // Default host: the real EF stores (so the user-scoped pool is exercised) plus a Graph
    // provider whose writer returns FakeTarget's calendars. The identity service uses the
    // cookie-flow defaults so SignInAsync produces a known user. No legacy account is seeded —
    // these tests are about the POOL becoming visible/pairable, not legacy regressions.
    private static WebApplicationFactory<Program> Build() =>
        new ServerTestFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMicrosoftTokenService>();
                services.AddSingleton<IMicrosoftTokenService>(
                    new CookieAuthHelper.FakeIdentityTokenService { Upn = "" });

                services.RemoveAll<ProviderRegistry>();
                services.AddSingleton(new ProviderRegistry(_ =>
                    new MicrosoftGraphProvider(new HttpClient(), new StubTokenProvider(), new FakeTarget())));

                services.RemoveAll<ISyncPairStore>();
                services.AddSingleton<ISyncPairStore, InMemorySyncPairStore>();
            });
        });

    private static Task<HttpClient> AuthedClientAsync(WebApplicationFactory<Program> factory) =>
        CookieAuthHelper.SignInAsync(factory);

    // The ZyncMaster user id for the cookie-flow identity (idempotent upsert returns the row
    // created during sign-in).
    private static async Task<string> CookieUserIdAsync(WebApplicationFactory<Program> factory)
    {
        var users = factory.Services.GetRequiredService<IUserStore>();
        var row = await users.UpsertAsync("microsoft", CookieSubject, CookieUpn, CookieDisplay);
        return row.Id;
    }

    // Inserts a pool calendar account directly for an explicit user id and returns its accountId.
    // Bypasses the OAuth connect flow (which is tested elsewhere) so a pool account can be planted
    // for either the caller or a foreign user.
    private static string SeedPoolAccount(
        WebApplicationFactory<Program> factory, string userId, string email, string display)
    {
        var id = Guid.NewGuid().ToString("N");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        db.CalendarAccounts.Add(new CalendarAccountRow
        {
            Id = id,
            UserId = userId,
            Kind = AccountKind.Graph.ToString(),
            Provider = "microsoft",
            AccountEmail = email,
            Scope = AccountScope.ReadWrite.ToString(),
            DisplayName = display,
            Status = "active",
            ConnectedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    private static string ForeignUserId(WebApplicationFactory<Program> factory)
    {
        var users = factory.Services.GetRequiredService<IUserStore>();
        return users.UpsertAsync("microsoft", "oid-other", "other@test", "Other User")
            .GetAwaiter().GetResult().Id;
    }

    // ---- GET /api/accounts -----------------------------------------------------------------

    [Fact]
    public async Task Accounts_lists_pool_account_with_its_accountId_as_ref()
    {
        using var factory = Build();
        var client = await AuthedClientAsync(factory);
        var userId = await CookieUserIdAsync(factory);
        var poolId = SeedPoolAccount(factory, userId, "pool@test", "Pool Account");

        var accounts = await client.GetFromJsonAsync<JsonElement>("/api/accounts");

        // The cookie sign-in flow also writes a legacy "default" account for the user, so the
        // union lists BOTH; the assertion that matters is that the POOL account surfaces with its
        // accountId as the ref (it was invisible before this change).
        var pool = accounts.EnumerateArray()
            .SingleOrDefault(a => a.GetProperty("accountRef").GetString() == poolId);
        pool.ValueKind.Should().NotBe(JsonValueKind.Undefined);
        pool.GetProperty("displayName").GetString().Should().Be("Pool Account");
    }

    [Fact]
    public async Task Accounts_unions_pool_and_legacy_accounts()
    {
        using var factory = Build();
        var client = await AuthedClientAsync(factory);
        var userId = await CookieUserIdAsync(factory);

        // A legacy single-account (UPN "default") + a distinct pool account for the same user.
        var legacy = factory.Services.GetRequiredService<IConnectedAccountStore>();
        await legacy.SetForUserAsync(userId, "default", "legacy-rt");
        var poolId = SeedPoolAccount(factory, userId, "pool@test", "Pool Account");

        var accounts = await client.GetFromJsonAsync<JsonElement>("/api/accounts");

        var refs = accounts.EnumerateArray().Select(a => a.GetProperty("accountRef").GetString()).ToList();
        refs.Should().Contain(poolId);
        refs.Should().Contain("default");
        refs.Should().HaveCount(2);
        // The legacy "default" account stays the implied default in a multi-account list.
        accounts.EnumerateArray()
            .Single(a => a.GetProperty("accountRef").GetString() == "default")
            .GetProperty("isDefault").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Accounts_excludes_other_users_pool_account()
    {
        using var factory = Build();
        var client = await AuthedClientAsync(factory);
        await CookieUserIdAsync(factory);

        // A pool account owned by a DIFFERENT user must not leak into the caller's listing.
        var foreign = ForeignUserId(factory);
        var foreignPoolId = SeedPoolAccount(factory, foreign, "foreign@test", "Foreign Account");

        var accounts = await client.GetFromJsonAsync<JsonElement>("/api/accounts");

        var refs = accounts.EnumerateArray().Select(a => a.GetProperty("accountRef").GetString()).ToList();
        refs.Should().NotContain(foreignPoolId);
        // The displayName of the foreign account must not leak either.
        accounts.EnumerateArray()
            .Select(a => a.GetProperty("displayName").GetString())
            .Should().NotContain("Foreign Account");
    }

    // ---- GET /api/accounts/{ref}/calendars -------------------------------------------------

    [Fact]
    public async Task Calendars_resolves_pool_account_via_adapter()
    {
        using var factory = Build();
        var client = await AuthedClientAsync(factory);
        var userId = await CookieUserIdAsync(factory);
        var poolId = SeedPoolAccount(factory, userId, "pool@test", "Pool Account");

        var cals = await client.GetFromJsonAsync<JsonElement>($"/api/accounts/{poolId}/calendars");

        cals.GetArrayLength().Should().Be(1);
        cals[0].GetProperty("id").GetString().Should().Be("poolcal1");
        cals[0].GetProperty("isDefault").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Calendars_for_other_users_pool_account_returns_404()
    {
        using var factory = Build();
        var client = await AuthedClientAsync(factory);
        await CookieUserIdAsync(factory);
        var foreign = ForeignUserId(factory);
        var foreignPoolId = SeedPoolAccount(factory, foreign, "foreign@test", "Foreign Account");

        var resp = await client.GetAsync($"/api/accounts/{foreignPoolId}/calendars");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- POST /api/pairs -------------------------------------------------------------------

    [Fact]
    public async Task Create_pair_accepts_pool_account_as_destination()
    {
        using var factory = Build();
        var client = await AuthedClientAsync(factory);
        var userId = await CookieUserIdAsync(factory);
        var poolId = SeedPoolAccount(factory, userId, "pool@test", "Pool Account");

        var body = new
        {
            name = "Pool dest pair",
            source = new { provider = "OutlookCom", calendarId = "src", calendarName = "Src" },
            destination = new { provider = "MicrosoftGraph", accountRef = poolId, calendarId = "poolcal1", calendarName = "Pool Primary" },
            intervalMin = 15,
        };

        var resp = await client.PostAsJsonAsync("/api/pairs", body);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().NotContain("unknown_destination_account");
    }

    [Fact]
    public async Task Create_pair_accepts_pool_account_on_both_sides_with_distinct_calendars()
    {
        using var factory = Build();
        var client = await AuthedClientAsync(factory);
        var userId = await CookieUserIdAsync(factory);
        var srcPoolId = SeedPoolAccount(factory, userId, "src-pool@test", "Source Pool");
        var dstPoolId = SeedPoolAccount(factory, userId, "dst-pool@test", "Dest Pool");

        var body = new
        {
            name = "Pool to pool",
            source = new { provider = "MicrosoftGraph", accountRef = srcPoolId, calendarId = "src-cal", calendarName = "Src" },
            destination = new { provider = "MicrosoftGraph", accountRef = dstPoolId, calendarId = "dst-cal", calendarName = "Dst" },
            intervalMin = 30,
        };

        var resp = await client.PostAsJsonAsync("/api/pairs", body);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Create_pair_rejects_other_users_pool_account()
    {
        using var factory = Build();
        var client = await AuthedClientAsync(factory);
        await CookieUserIdAsync(factory);
        var foreign = ForeignUserId(factory);
        var foreignPoolId = SeedPoolAccount(factory, foreign, "foreign@test", "Foreign Account");

        var body = new
        {
            name = "Cross-user pool pair",
            source = new { provider = "OutlookCom", calendarId = "src", calendarName = "Src" },
            destination = new { provider = "MicrosoftGraph", accountRef = foreignPoolId, calendarId = "poolcal1", calendarName = "Pool Primary" },
            intervalMin = 15,
        };

        var resp = await client.PostAsJsonAsync("/api/pairs", body);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("destination.accountRef");
    }
}
