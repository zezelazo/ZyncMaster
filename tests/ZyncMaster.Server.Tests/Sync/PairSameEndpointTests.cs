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
using ZyncMaster.Graph;
using Xunit;

namespace ZyncMaster.Server.Tests.Sync;

// §B-4 — POST /api/pairs must reject a pair whose source and destination address the same
// calendar (same account + same calendar id), so a run can never sweep the calendar it reads.
// Distinct accounts, or the same account with different calendars, are allowed.
public sealed class PairSameEndpointTests
{
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
                    new MicrosoftGraphProvider(new HttpClient(), new StubTokenProvider(), new FakeTarget())));

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

    private static Task<HttpClient> AuthedClientAsync(WebApplicationFactory<Program> factory) =>
        CookieAuthHelper.SignInAsync(factory);

    [Fact]
    public async Task Create_with_same_graph_account_and_calendar_returns_400_same_source_destination()
    {
        var factory = Build();
        var client = await AuthedClientAsync(factory);

        var body = new
        {
            name = "Loop",
            source = new { provider = "MicrosoftGraph", accountRef = "default", calendarId = "cal1" },
            destination = new { provider = "MicrosoftGraph", accountRef = "default", calendarId = "cal1" },
            intervalMin = 15,
        };

        var resp = await client.PostAsJsonAsync("/api/pairs", body);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetString().Should().Be("same_source_destination");
    }

    [Fact]
    public async Task Create_with_same_account_but_different_calendars_is_allowed()
    {
        var factory = Build();
        var client = await AuthedClientAsync(factory);

        var body = new
        {
            name = "Cross-calendar",
            source = new { provider = "MicrosoftGraph", accountRef = "default", calendarId = "cal1" },
            destination = new { provider = "MicrosoftGraph", accountRef = "default", calendarId = "cal2" },
            intervalMin = 15,
        };

        var resp = await client.PostAsJsonAsync("/api/pairs", body);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Create_with_outlookcom_source_and_graph_destination_is_allowed()
    {
        var factory = Build();
        var client = await AuthedClientAsync(factory);

        var body = new
        {
            name = "Device pair",
            source = new { provider = "OutlookCom", accountRef = "default", calendarId = "cal1" },
            destination = new { provider = "MicrosoftGraph", accountRef = "default", calendarId = "cal1" },
            intervalMin = 15,
        };

        var resp = await client.PostAsJsonAsync("/api/pairs", body);

        // Different providers => not the same account; this is the normal device→cloud pair.
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
