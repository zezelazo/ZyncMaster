using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    public async Task Delete_account_disables_referencing_pairs_and_returns_ids()
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

        (await pairs.GetAsync("p-dest"))!.State.Should().Be("disabled");
        (await pairs.GetAsync("p-src"))!.State.Should().Be("disabled");
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
}
