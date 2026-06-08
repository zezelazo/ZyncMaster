using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ZyncMaster.Server.Data;
using Xunit;

namespace ZyncMaster.Server.Tests.Sync;

public class AccountUnlinkTests : IClassFixture<ServerTestFactory>
{
    private readonly ServerTestFactory _factory;

    public AccountUnlinkTests(ServerTestFactory factory) => _factory = factory;

    private static WebApplicationFactory<Program> Build() =>
        new ServerTestFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMicrosoftTokenService>();
                services.AddSingleton<IMicrosoftTokenService>(new CookieAuthHelper.FakeIdentityTokenService());

                services.RemoveAll<IConnectedAccountStore>();
                services.AddSingleton<IConnectedAccountStore>(_ =>
                {
                    var store = new DataProtectionConnectedAccountStore(DataProtectionProvider.Create("tests"));
                    store.SetAsync("alice@test", "rt-a").GetAwaiter().GetResult();
                    store.SetAsync("bob@test", "rt-b").GetAwaiter().GetResult();
                    return store;
                });

                services.RemoveAll<ISyncPairStore>();
                services.AddSingleton<ISyncPairStore, InMemorySyncPairStore>();
            });
        });

    // Pairs-management endpoints are cookie-gated, so authenticate via the real OAuth flow.
    private static Task<HttpClient> AuthedClientAsync(WebApplicationFactory<Program> factory) =>
        CookieAuthHelper.SignInAsync(factory);

    private static SyncPair Pair(string id, string? src, string? dst) => new()
    {
        Id = id,
        Name = id,
        Source = new Endpoint { Provider = "MicrosoftGraph", AccountRef = src, CalendarId = "s" },
        Destination = new Endpoint { Provider = "MicrosoftGraph", AccountRef = dst, CalendarId = "d" },
        IntervalMin = 10,
    };

    [Fact]
    public async Task Delete_account_deletes_referencing_pairs_and_returns_ids()
    {
        var factory = Build();
        var pairs = factory.Services.GetRequiredService<ISyncPairStore>();
        await pairs.AddAsync(Pair("p-dest", src: "bob@test", dst: "alice@test"));
        await pairs.AddAsync(Pair("p-src", src: "alice@test", dst: "bob@test"));
        await pairs.AddAsync(Pair("p-unrelated", src: "bob@test", dst: "bob@test"));
        var client = await AuthedClientAsync(factory);

        var resp = await client.DeleteAsync("/api/accounts/alice@test");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var ids = doc.RootElement.GetProperty("affectedPairIds").EnumerateArray().Select(e => e.GetString()).ToList();
        ids.Should().BeEquivalentTo(new[] { "p-dest", "p-src" });

        // Forget now DELETES the referencing pairs (not disables them): they are gone from the store.
        (await pairs.GetAsync("p-dest")).Should().BeNull();
        (await pairs.GetAsync("p-src")).Should().BeNull();
        (await pairs.GetAsync("p-unrelated"))!.State.Should().Be("active");
    }

    [Fact]
    public async Task Delete_account_removes_it_from_store()
    {
        var factory = Build();
        var accounts = factory.Services.GetRequiredService<IConnectedAccountStore>();
        var client = await AuthedClientAsync(factory);

        await client.DeleteAsync("/api/accounts/alice@test");

        (await accounts.GetAsync("alice@test")).Should().BeNull();
        (await accounts.GetAsync("bob@test")).Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_account_dedupes_pair_referenced_on_both_sides()
    {
        var factory = Build();
        var pairs = factory.Services.GetRequiredService<ISyncPairStore>();
        await pairs.AddAsync(Pair("p-both", src: "alice@test", dst: "alice@test"));
        var client = await AuthedClientAsync(factory);

        var resp = await client.DeleteAsync("/api/accounts/alice@test");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var ids = doc.RootElement.GetProperty("affectedPairIds").EnumerateArray().Select(e => e.GetString()).ToList();
        ids.Should().BeEquivalentTo(new[] { "p-both" });
    }

    [Fact]
    public async Task Delete_account_with_no_pairs_returns_empty()
    {
        var factory = Build();
        var client = await AuthedClientAsync(factory);

        var resp = await client.DeleteAsync("/api/accounts/alice@test");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("affectedPairIds").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Delete_account_requires_cookie()
    {
        var factory = Build();
        var client = factory.CreateClient();

        (await client.DeleteAsync("/api/accounts/alice@test")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Delete_account_rejects_api_key()
    {
        var factory = Build();
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

        // Cookie-gated endpoint must not accept a device api key.
        (await client.DeleteAsync("/api/accounts/alice@test")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- pool-account delete (BUG: DELETE /api/accounts/{ref} was legacy-only) --------------
    //
    // The unified surface returns pool accounts with AccountRef = poolAccount.Id (a random Guid).
    // The DELETE handler must resolve that id through the adapter (pool-first) and delete from the
    // POOL store, disabling any referencing pair on the canonical accountId. Before the fix it ran
    // IConnectedAccountStore.GetAsync(ref) only, so a pool id 404'd and the App could not unlink a
    // freshly connected account.

    // Identity the cookie sign-in flow mints (CookieAuthHelper defaults).
    private const string CookieSubject = "oid-123";
    private const string CookieUpn = "user@test";
    private const string CookieDisplay = "Test User";

    // Pool-aware host: real EF pool store (so the user-scoped pool is exercised) + in-memory pairs.
    // No legacy seed — these tests are about the POOL delete path.
    private static WebApplicationFactory<Program> BuildPool() =>
        new ServerTestFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IMicrosoftTokenService>();
                services.AddSingleton<IMicrosoftTokenService>(
                    new CookieAuthHelper.FakeIdentityTokenService { Upn = "" });

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

    private static string ForeignUserId(WebApplicationFactory<Program> factory)
    {
        var users = factory.Services.GetRequiredService<IUserStore>();
        return users.UpsertAsync("microsoft", "oid-other", "other@test", "Other User")
            .GetAwaiter().GetResult().Id;
    }

    // Inserts a pool calendar account directly for an explicit user id and returns its accountId.
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
    public async Task Delete_pool_account_unlinks_and_deletes_pairs()
    {
        using var factory = BuildPool();
        var client = await AuthedClientAsync(factory);
        var userId = await CookieUserIdAsync(factory);
        var poolId = SeedPoolAccount(factory, userId, "pool@test", "Pool Account");

        // A pair that references the pool account by its accountId as the destination.
        var pairs = factory.Services.GetRequiredService<ISyncPairStore>();
        await pairs.AddAsync(new SyncPair
        {
            Id = "p-pool",
            Name = "p-pool",
            Source = new Endpoint { Provider = "OutlookCom", AccountRef = "dev", CalendarId = "s" },
            Destination = new Endpoint { Provider = "MicrosoftGraph", AccountRef = poolId, CalendarId = "d" },
            IntervalMin = 10,
        });

        // The pool account is visible on the unified listing before the delete.
        var before = await client.GetFromJsonAsync<JsonElement>("/api/accounts");
        before.EnumerateArray().Select(a => a.GetProperty("accountRef").GetString())
            .Should().Contain(poolId);

        var resp = await client.DeleteAsync($"/api/accounts/{poolId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("affectedPairIds").EnumerateArray()
            .Select(e => e.GetString()).Should().BeEquivalentTo(new[] { "p-pool" });

        // The account is gone from the pool (so gone from the unified listing) and the pair is deleted.
        var after = await client.GetFromJsonAsync<JsonElement>("/api/accounts");
        after.EnumerateArray().Select(a => a.GetProperty("accountRef").GetString())
            .Should().NotContain(poolId);
        (await pairs.GetAsync("p-pool")).Should().BeNull();
    }

    [Fact]
    public async Task Delete_other_users_pool_account_returns_404_and_keeps_it()
    {
        using var factory = BuildPool();
        var client = await AuthedClientAsync(factory);
        await CookieUserIdAsync(factory);
        var foreign = ForeignUserId(factory);
        var foreignPoolId = SeedPoolAccount(factory, foreign, "foreign@test", "Foreign Account");

        var resp = await client.DeleteAsync($"/api/accounts/{foreignPoolId}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        db.CalendarAccounts.Any(a => a.Id == foreignPoolId).Should().BeTrue();
    }
}
