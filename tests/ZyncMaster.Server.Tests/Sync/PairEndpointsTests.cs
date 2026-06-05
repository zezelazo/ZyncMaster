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
using ZyncMaster.Graph;
using Xunit;

namespace ZyncMaster.Server.Tests.Sync;

public class PairEndpointsTests : IClassFixture<ServerTestFactory>
{
    private readonly ServerTestFactory _factory;

    public PairEndpointsTests(ServerTestFactory factory) => _factory = factory;

    private sealed class FakeTarget : ICalendarTarget
    {
        public IReadOnlyList<CalendarTargetInfo> Calendars { get; set; } = new[]
        {
            new CalendarTargetInfo { Id = "cal1", DisplayName = "Primary", IsDefault = true, Owner = "me@test" },
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

    // Entitlements stub for the plan-cap gate test: returns whatever caps the test asks for.
    private sealed class StubEntitlementsService : IEntitlementsService
    {
        private readonly ZyncMaster.Server.Entitlements _entitlements;
        public StubEntitlementsService(ZyncMaster.Server.Entitlements entitlements) => _entitlements = entitlements;
        public Task<ZyncMaster.Server.Entitlements> GetForUserAsync(string userId, CancellationToken ct = default) =>
            Task.FromResult(_entitlements);
    }

    private static WebApplicationFactory<Program> Build(bool seedAccount = true, ZyncMaster.Server.Entitlements? entitlements = null) =>
        new ServerTestFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                if (entitlements is not null)
                {
                    services.RemoveAll<IEntitlementsService>();
                    services.AddSingleton<IEntitlementsService>(new StubEntitlementsService(entitlements));
                }

                services.RemoveAll<IMicrosoftTokenService>();
                // Empty UPN so the callback's connected-account write normalizes to the
                // "default" key and overwrites the seed rather than adding a second account
                // (the account-listing assertions expect exactly the one seeded account).
                services.AddSingleton<IMicrosoftTokenService>(
                    new CookieAuthHelper.FakeIdentityTokenService { Upn = "" });

                services.RemoveAll<ProviderRegistry>();
                services.AddSingleton(new ProviderRegistry(_ =>
                    new MicrosoftGraphProvider(new HttpClient(), new StubTokenProvider(), new FakeTarget())));

                services.RemoveAll<IConnectedAccountStore>();
                services.AddSingleton<IConnectedAccountStore>(_ =>
                {
                    var store = new DataProtectionConnectedAccountStore(DataProtectionProvider.Create("tests"));
                    if (seedAccount)
                        store.SetAsync("default", "rt").GetAwaiter().GetResult();
                    return store;
                });

                services.RemoveAll<ISyncPairStore>();
                services.AddSingleton<ISyncPairStore, InMemorySyncPairStore>();
            });
        });

    // Pairs-management endpoints (accounts, calendars, pair CRUD) are cookie-gated;
    // authenticate via the real OAuth sign-in flow to obtain a session cookie.
    private static Task<HttpClient> AuthedClientAsync(WebApplicationFactory<Program> factory) =>
        CookieAuthHelper.SignInAsync(factory);

    private static object MakeCreateBody(string name = "My pair", int interval = 15) => new
    {
        name,
        source = new { provider = "OutlookCom", calendarId = "src", calendarName = "Src" },
        destination = new { provider = "MicrosoftGraph", accountRef = "default", calendarId = "cal1", calendarName = "Primary" },
        intervalMin = interval,
    };

    [Fact]
    public async Task Pairs_endpoints_require_cookie()
    {
        var factory = Build();
        var client = factory.CreateClient();

        (await client.GetAsync("/api/pairs")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/accounts")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Pairs_endpoints_reject_api_key()
    {
        var factory = Build();
        var deviceStore = factory.Services.GetRequiredService<IDeviceStore>();
        var key = ApiKeyGenerator.Generate();
        await deviceStore.AddAsync(new Device
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Laptop",
            ApiKeyHash = ApiKeyHasher.Hash(key),
            CreatedUtc = DateTimeOffset.UtcNow,
        });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);

        // A device api key must not unlock the human-only pairs-management surface.
        (await client.GetAsync("/api/pairs")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/accounts")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_list_patch_delete_round_trip()
    {
        var factory = Build();
        var client = await AuthedClientAsync(factory);

        var create = await client.PostAsJsonAsync("/api/pairs", MakeCreateBody());
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var id = created.RootElement.GetProperty("id").GetString()!;
        created.RootElement.GetProperty("state").GetString().Should().Be("active");
        created.RootElement.GetProperty("name").GetString().Should().Be("My pair");

        var list = await client.GetFromJsonAsync<JsonElement>("/api/pairs");
        list.GetArrayLength().Should().Be(1);

        var patch = await client.PatchAsJsonAsync($"/api/pairs/{id}", new { name = "Renamed", intervalMin = 60, state = "paused" });
        patch.StatusCode.Should().Be(HttpStatusCode.OK);
        using var patched = JsonDocument.Parse(await patch.Content.ReadAsStringAsync());
        patched.RootElement.GetProperty("name").GetString().Should().Be("Renamed");
        patched.RootElement.GetProperty("intervalMin").GetInt32().Should().Be(60);
        patched.RootElement.GetProperty("state").GetString().Should().Be("paused");

        var del = await client.DeleteAsync($"/api/pairs/{id}");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterList = await client.GetFromJsonAsync<JsonElement>("/api/pairs");
        afterList.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Create_with_empty_name_returns_400()
    {
        var factory = Build();
        var client = await AuthedClientAsync(factory);

        var resp = await client.PostAsJsonAsync("/api/pairs", MakeCreateBody(name: ""));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_with_zero_interval_returns_400()
    {
        var factory = Build();
        var client = await AuthedClientAsync(factory);

        var resp = await client.PostAsJsonAsync("/api/pairs", MakeCreateBody(interval: 0));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_with_missing_destination_calendar_returns_400()
    {
        var factory = Build();
        var client = await AuthedClientAsync(factory);

        var body = new
        {
            name = "X",
            source = new { provider = "OutlookCom", calendarId = "src" },
            destination = new { provider = "MicrosoftGraph", calendarId = "" },
            intervalMin = 5,
        };

        var resp = await client.PostAsJsonAsync("/api/pairs", body);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_unknown_id_returns_404()
    {
        var factory = Build();
        var client = await AuthedClientAsync(factory);

        var resp = await client.PatchAsJsonAsync("/api/pairs/missing", new { name = "X" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Patch_invalid_state_returns_400()
    {
        var factory = Build();
        var client = await AuthedClientAsync(factory);

        var create = await client.PostAsJsonAsync("/api/pairs", MakeCreateBody());
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var id = created.RootElement.GetProperty("id").GetString()!;

        var resp = await client.PatchAsJsonAsync($"/api/pairs/{id}", new { state = "bogus" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_with_unknown_destination_account_returns_400()
    {
        var factory = Build();
        var client = await AuthedClientAsync(factory);

        // The seeded store only owns the "default" account; referencing a foreign /
        // nonexistent accountRef on a Graph destination must be rejected with a clean 400.
        var body = new
        {
            name = "Cross-user pair",
            source = new { provider = "OutlookCom", calendarId = "src", calendarName = "Src" },
            destination = new { provider = "MicrosoftGraph", accountRef = "someone-else@test", calendarId = "cal1", calendarName = "Primary" },
            intervalMin = 15,
        };

        var resp = await client.PostAsJsonAsync("/api/pairs", body);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("destination.accountRef");
    }

    [Fact]
    public async Task Create_with_unknown_source_account_returns_400()
    {
        var factory = Build();
        var client = await AuthedClientAsync(factory);

        var body = new
        {
            name = "Bad source pair",
            source = new { provider = "MicrosoftGraph", accountRef = "ghost@test", calendarId = "src", calendarName = "Src" },
            destination = new { provider = "MicrosoftGraph", accountRef = "default", calendarId = "cal1", calendarName = "Primary" },
            intervalMin = 15,
        };

        var resp = await client.PostAsJsonAsync("/api/pairs", body);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("source.accountRef");
    }

    [Fact]
    public async Task Accounts_lists_connected_account_as_default()
    {
        var factory = Build();
        var client = await AuthedClientAsync(factory);

        var accounts = await client.GetFromJsonAsync<JsonElement>("/api/accounts");

        accounts.GetArrayLength().Should().Be(1);
        accounts[0].GetProperty("accountRef").GetString().Should().Be("default");
        accounts[0].GetProperty("isDefault").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Calendars_returns_writer_calendars()
    {
        var factory = Build();
        var client = await AuthedClientAsync(factory);

        var cals = await client.GetFromJsonAsync<JsonElement>("/api/accounts/default/calendars");

        cals.GetArrayLength().Should().Be(1);
        cals[0].GetProperty("id").GetString().Should().Be("cal1");
        cals[0].GetProperty("isDefault").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task CreateCalendar_creates_and_returns_the_calendar()
    {
        var factory = Build();
        var client = await AuthedClientAsync(factory);

        var resp = await client.PostAsJsonAsync("/api/accounts/default/calendars", new { name = "Travel" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var created = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        // FakeTarget echoes the name as the display name and assigns "n" as the id.
        created.RootElement.GetProperty("id").GetString().Should().Be("n");
        created.RootElement.GetProperty("displayName").GetString().Should().Be("Travel");
    }

    [Fact]
    public async Task CreateCalendar_with_empty_name_returns_400()
    {
        var factory = Build();
        var client = await AuthedClientAsync(factory);

        var resp = await client.PostAsJsonAsync("/api/accounts/default/calendars", new { name = "" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateCalendar_for_unknown_account_returns_404()
    {
        var factory = Build();
        var client = await AuthedClientAsync(factory);

        // The seeded store only owns "default"; a foreign ref must 404 (no existence leak),
        // never hit Graph with another user's missing token.
        var resp = await client.PostAsJsonAsync("/api/accounts/someone-else@test/calendars", new { name = "X" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateCalendar_requires_cookie()
    {
        var factory = Build();
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/accounts/default/calendars", new { name = "X" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_over_plan_pair_cap_returns_402_plan_limit_reached()
    {
        // FIX H — wire the entitlements gate. With a cap of one pair, the first create succeeds and
        // the second is rejected with 402 plan_limit_reached (the user is already at the cap).
        var factory = Build(entitlements: new ZyncMaster.Server.Entitlements { MaxPairs = 1 });
        var client = await AuthedClientAsync(factory);

        var first = await client.PostAsJsonAsync("/api/pairs", MakeCreateBody(name: "Pair one"));
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await client.PostAsJsonAsync("/api/pairs", MakeCreateBody(name: "Pair two"));
        second.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
        using var doc = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetString().Should().Be("plan_limit_reached");

        // The cap held: the second pair was never persisted.
        var list = await client.GetFromJsonAsync<JsonElement>("/api/pairs");
        list.GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Create_clamps_interval_up_to_plan_floor()
    {
        // FIX H — MinSyncIntervalMinutes is a floor: a request below it is raised to the floor rather
        // than rejected, so a paid floor never hard-fails pair creation on interval alone.
        var factory = Build(entitlements: new ZyncMaster.Server.Entitlements { MinSyncIntervalMinutes = 30 });
        var client = await AuthedClientAsync(factory);

        var resp = await client.PostAsJsonAsync("/api/pairs", MakeCreateBody(interval: 5));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("intervalMin").GetInt32().Should().Be(30);
    }
}
