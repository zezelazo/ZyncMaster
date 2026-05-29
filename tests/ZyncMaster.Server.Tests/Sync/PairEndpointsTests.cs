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

public class PairEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public PairEndpointsTests(WebApplicationFactory<Program> factory) => _factory = factory;

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

    private static WebApplicationFactory<Program> Build(bool seedAccount = true) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
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

    private static object MakeCreateBody(string name = "My pair", int interval = 15) => new
    {
        name,
        source = new { provider = "OutlookCom", calendarId = "src", calendarName = "Src" },
        destination = new { provider = "MicrosoftGraph", accountRef = "default", calendarId = "cal1", calendarName = "Primary" },
        intervalMin = interval,
    };

    [Fact]
    public async Task Pairs_endpoints_require_api_key()
    {
        var factory = Build();
        var client = factory.CreateClient();

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
}
