using System;
using System.Collections.Generic;
using System.Linq;
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

// FU-3 — /api/accounts must collapse the SAME mailbox (casilla) when it exists in BOTH the new pool
// (ICalendarAccountStore, random-Guid id) and the legacy store (IConnectedAccountStore, UPN keyed),
// because their ids never coincide and the account would otherwise be listed twice. Dedup is BY
// normalized email; the POOL representation wins (its AccountRef = pool accountId resolves directly
// for createPair/sync). A legacy "default" account has no real email and must NOT be fused.
public class AccountEmailDedupeTests
{
    private const string CookieSubject = "oid-123";
    private const string CookieUpn = "user@test";
    private const string CookieDisplay = "Test User";

    private sealed class FakeTarget : ICalendarTarget
    {
        public Task<IReadOnlyList<CalendarTargetInfo>> ListCalendarsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CalendarTargetInfo>>(Array.Empty<CalendarTargetInfo>());
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
                    new MicrosoftGraphProvider(new System.Net.Http.HttpClient(), new StubTokenProvider(), new FakeTarget())));

                services.RemoveAll<ISyncPairStore>();
                services.AddSingleton<ISyncPairStore, InMemorySyncPairStore>();
            });
        });

    private static async Task<string> CookieUserIdAsync(WebApplicationFactory<Program> factory)
    {
        var users = factory.Services.GetRequiredService<IUserStore>();
        var row = await users.UpsertAsync("microsoft", CookieSubject, CookieUpn, CookieDisplay);
        return row.Id;
    }

    // Inserts a pool calendar account for the user with an explicit mailbox email and returns its id.
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

    [Fact]
    public async Task Same_mailbox_in_pool_and_legacy_collapses_to_the_pool_entry()
    {
        using var factory = Build();
        var client = await CookieAuthHelper.SignInAsync(factory);
        var userId = await CookieUserIdAsync(factory);

        // Same casilla "shared@test" in BOTH stores: pool (random Guid id) and legacy (UPN keyed).
        // Casing differs to prove the dedup is case-insensitive.
        var poolId = SeedPoolAccount(factory, userId, "shared@test", "Shared Pool");
        var legacy = factory.Services.GetRequiredService<IConnectedAccountStore>();
        await legacy.SetForUserAsync(userId, "Shared@TEST", "legacy-rt");

        var accounts = await client.GetFromJsonAsync<JsonElement>("/api/accounts");

        // The shared mailbox surfaces ONCE, as the POOL representation (its ref is the pool
        // accountId, NOT the legacy UPN), so createPair/sync keep resolving it. The cookie sign-in
        // also writes a separate legacy "default" account, so the listing additionally carries that
        // distinct entry — what matters here is the shared casilla is not duplicated.
        var refs = accounts.EnumerateArray().Select(a => a.GetProperty("accountRef").GetString()).ToList();
        refs.Should().Contain(poolId);
        refs.Should().NotContain("Shared@TEST", "the legacy representation of the shared mailbox is collapsed into the pool entry");
        accounts.EnumerateArray()
            .Single(a => a.GetProperty("accountRef").GetString() == poolId)
            .GetProperty("displayName").GetString().Should().Be("Shared Pool");
    }

    [Fact]
    public async Task Distinct_mailboxes_in_pool_and_legacy_stay_two_entries()
    {
        using var factory = Build();
        var client = await CookieAuthHelper.SignInAsync(factory);
        var userId = await CookieUserIdAsync(factory);

        var poolId = SeedPoolAccount(factory, userId, "pool@test", "Pool Account");
        var legacy = factory.Services.GetRequiredService<IConnectedAccountStore>();
        await legacy.SetForUserAsync(userId, "legacy@test", "legacy-rt");

        var accounts = await client.GetFromJsonAsync<JsonElement>("/api/accounts");

        // Different mailboxes are NOT collapsed: the pool account and the distinct legacy account
        // both surface (alongside the sign-in's separate "default" legacy account).
        var refs = accounts.EnumerateArray().Select(a => a.GetProperty("accountRef").GetString()).ToList();
        refs.Should().Contain(poolId);
        refs.Should().Contain("legacy@test");
    }

    [Fact]
    public async Task Legacy_default_without_real_email_is_not_fused_with_a_pool_account()
    {
        using var factory = Build();
        var client = await CookieAuthHelper.SignInAsync(factory);
        var userId = await CookieUserIdAsync(factory);

        // A legacy "default" single-account (no comparable email) + a real pool account. The blank
        // mailbox of "default" must NOT collapse it into the pool account: both must stay listed.
        var poolId = SeedPoolAccount(factory, userId, "pool@test", "Pool Account");
        var legacy = factory.Services.GetRequiredService<IConnectedAccountStore>();
        await legacy.SetForUserAsync(userId, "default", "legacy-rt");

        var accounts = await client.GetFromJsonAsync<JsonElement>("/api/accounts");

        var refs = accounts.EnumerateArray().Select(a => a.GetProperty("accountRef").GetString()).ToList();
        refs.Should().HaveCount(2);
        refs.Should().Contain(poolId);
        refs.Should().Contain("default");
    }
}
