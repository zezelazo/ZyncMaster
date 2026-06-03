using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
using ZyncMaster.Server.Modules.Calendar;
using Xunit;

namespace ZyncMaster.Server.Tests.Sync;

// The pairs/accounts MANAGEMENT surface is human-only and now accepts the Cookie OR the
// IdentityBearer scheme (RequireCookieOrIdentityBearer) — the device api key stays rejected
// (covered by PairEndpointsTests.Pairs_endpoints_reject_api_key). These tests prove the App's
// path: a valid IDENTITY BEARER is accepted on /api/accounts, /api/accounts/{ref}/calendars and
// /api/pairs (GET + POST), and every route stays user-scoped (one user never sees another's
// account or pair). The bearers come from the real IIdentityTokenService; the connect flow that
// seeds a pool account is driven exactly like CalendarConnectEndpointsTests.
public class PairEndpointsIdentityBearerTests
{
    private const string CalendarStateCookieName = "sm_calendar_oauth_state";

    private sealed class FakeTokenService : IMicrosoftTokenService
    {
        public string Email { get; init; } = "calendar@test";
        public string Refresh { get; init; } = "calendar-refresh-token";

        public Task<TokenResult> ExchangeCalendarCodeAsync(string code, string scopes, CancellationToken ct = default) =>
            Task.FromResult(new TokenResult
            {
                AccessToken = "calendar-access-token",
                RefreshToken = Refresh,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
                UserPrincipalName = Email,
                Subject = "calendar-oid",
                Email = Email,
                DisplayName = "Calendar Owner",
            });

        public Task<TokenResult> ExchangeCodeAsync(string code, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<TokenResult> ExchangeIdentityCodeAsync(string code, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<TokenResult> RefreshAsync(string refreshToken, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeTarget : ICalendarTarget
    {
        public Task<IReadOnlyList<CalendarTargetInfo>> ListCalendarsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CalendarTargetInfo>>(new[]
            {
                new CalendarTargetInfo { Id = "cal1", DisplayName = "Primary", IsDefault = true, Owner = "me@test" },
            });
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

    // Real server composition + real EF stores; only the Microsoft token boundary and the Graph
    // provider are faked so the connect flow and the calendars listing resolve without live Graph.
    private static WebApplicationFactory<Program> Build() =>
        new ServerTestFactory().WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IMicrosoftTokenService>();
            s.AddSingleton<IMicrosoftTokenService>(new FakeTokenService());

            s.RemoveAll<ProviderRegistry>();
            s.AddSingleton(new ProviderRegistry(_ =>
                new MicrosoftGraphProvider(new HttpClient(), new StubTokenProvider(), new FakeTarget())));
        }));

    private static HttpClient NoRedirectClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static (string token, string userId) IssueBearer(
        WebApplicationFactory<Program> factory, string subject, string email)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserStore>();
        var tokens = scope.ServiceProvider.GetRequiredService<IIdentityTokenService>();
        var user = users.UpsertByLoginAsync(
            "local", subject, email, emailVerified: true, displayName: subject, CancellationToken.None)
            .GetAwaiter().GetResult();
        return (tokens.IssueAccessToken(user).Token, user.Id);
    }

    private static HttpRequestMessage Bearer(HttpMethod method, string url, string? token)
    {
        var req = new HttpRequestMessage(method, url);
        if (token is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private static HttpRequestMessage BearerJson(HttpMethod method, string url, string token, object body)
    {
        var req = Bearer(method, url, token);
        req.Content = JsonContent.Create(body);
        return req;
    }

    private static string ExtractCookie(HttpResponseMessage response, string name) =>
        response.Headers.GetValues("Set-Cookie")
            .First(c => c.StartsWith(name + "=", StringComparison.Ordinal))
            .Split(';')[0];

    private static string ExtractQueryValue(Uri location, string key)
    {
        foreach (var pair in location.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (Uri.UnescapeDataString(pair[..idx]) == key)
                return Uri.UnescapeDataString(pair[(idx + 1)..]);
        }
        throw new InvalidOperationException($"{key} not found in {location}");
    }

    // Drives connect/graph + callback under the bearer so exactly one pool account is persisted
    // for the caller. Returns that account's accountRef (its pool id).
    private static async Task<string> ConnectAccountAsync(
        HttpClient client, WebApplicationFactory<Program> factory, string token, string userId, int port)
    {
        var connect = await client.SendAsync(Bearer(
            HttpMethod.Get, $"/calendar/connect/graph?scope=readwrite&port={port}&nonce=n", token));
        connect.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var state = ExtractQueryValue(connect.Headers.Location!, "state");
        var csrfCookie = ExtractCookie(connect, CalendarStateCookieName);

        var callback = Bearer(HttpMethod.Get,
            $"/calendar/connect/callback/graph?code=abc&state={Uri.EscapeDataString(state)}", null);
        callback.Headers.Add("Cookie", csrfCookie);
        (await client.SendAsync(callback)).StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        return db.CalendarAccounts.Single(a => a.UserId == userId).Id;
    }

    private static object ComPairBody(string name, string accountRef, string calId = "cal1") => new
    {
        name,
        source = new { provider = "OutlookCom", calendarId = "src", calendarName = "Src" },
        destination = new { provider = "MicrosoftGraph", accountRef, calendarId = calId, calendarName = "Primary" },
        intervalMin = 15,
    };

    // ---- /api/accounts ---------------------------------------------------------------------

    [Fact]
    public async Task Accounts_accepts_identity_bearer_and_is_user_scoped()
    {
        using var factory = Build();
        var (tokenA, userIdA) = IssueBearer(factory, "ib-acc-a", "ib-acc-a@test");
        var (tokenB, _) = IssueBearer(factory, "ib-acc-b", "ib-acc-b@test");
        var client = NoRedirectClient(factory);

        var accountRefA = await ConnectAccountAsync(client, factory, tokenA, userIdA, 52010);

        // A's bearer lists A's account.
        var listA = await client.SendAsync(Bearer(HttpMethod.Get, "/api/accounts", tokenA));
        listA.StatusCode.Should().Be(HttpStatusCode.OK);
        using var docA = JsonDocument.Parse(await listA.Content.ReadAsStringAsync());
        docA.RootElement.EnumerateArray()
            .Select(e => e.GetProperty("accountRef").GetString())
            .Should().Contain(accountRefA);

        // B's bearer never sees A's account.
        var listB = await client.SendAsync(Bearer(HttpMethod.Get, "/api/accounts", tokenB));
        listB.StatusCode.Should().Be(HttpStatusCode.OK);
        using var docB = JsonDocument.Parse(await listB.Content.ReadAsStringAsync());
        docB.RootElement.GetArrayLength().Should().Be(0);
    }

    // ---- /api/accounts/{ref}/calendars -----------------------------------------------------

    [Fact]
    public async Task Calendars_accepts_identity_bearer_for_owned_account()
    {
        using var factory = Build();
        var (tokenA, userIdA) = IssueBearer(factory, "ib-cal-a", "ib-cal-a@test");
        var client = NoRedirectClient(factory);

        var accountRefA = await ConnectAccountAsync(client, factory, tokenA, userIdA, 52020);

        var cals = await client.SendAsync(Bearer(
            HttpMethod.Get, $"/api/accounts/{Uri.EscapeDataString(accountRefA)}/calendars", tokenA));
        cals.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await cals.Content.ReadAsStringAsync());
        doc.RootElement[0].GetProperty("id").GetString().Should().Be("cal1");
    }

    [Fact]
    public async Task Calendars_cross_user_account_returns_404_under_bearer()
    {
        using var factory = Build();
        var (tokenA, userIdA) = IssueBearer(factory, "ib-cal-x-a", "ib-cal-x-a@test");
        var (tokenB, _) = IssueBearer(factory, "ib-cal-x-b", "ib-cal-x-b@test");
        var client = NoRedirectClient(factory);

        var accountRefA = await ConnectAccountAsync(client, factory, tokenA, userIdA, 52021);

        // B asks for A's account's calendars: not owned -> 404 (never leak existence).
        var resp = await client.SendAsync(Bearer(
            HttpMethod.Get, $"/api/accounts/{Uri.EscapeDataString(accountRefA)}/calendars", tokenB));
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- /api/pairs (GET + POST) -----------------------------------------------------------

    [Fact]
    public async Task Pairs_post_then_get_accepts_identity_bearer_and_is_user_scoped()
    {
        using var factory = Build();
        var (tokenA, userIdA) = IssueBearer(factory, "ib-pair-a", "ib-pair-a@test");
        var (tokenB, _) = IssueBearer(factory, "ib-pair-b", "ib-pair-b@test");
        var client = NoRedirectClient(factory);

        var accountRefA = await ConnectAccountAsync(client, factory, tokenA, userIdA, 52030);

        // POST a pair under A's bearer.
        var create = await client.SendAsync(BearerJson(
            HttpMethod.Post, "/api/pairs", tokenA, ComPairBody("Bearer pair", accountRefA)));
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        using var created = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        var pairId = created.RootElement.GetProperty("id").GetString()!;

        // GET under A's bearer lists exactly that pair.
        var listA = await client.SendAsync(Bearer(HttpMethod.Get, "/api/pairs", tokenA));
        listA.StatusCode.Should().Be(HttpStatusCode.OK);
        using var docA = JsonDocument.Parse(await listA.Content.ReadAsStringAsync());
        docA.RootElement.GetArrayLength().Should().Be(1);
        docA.RootElement[0].GetProperty("id").GetString().Should().Be(pairId);

        // B's bearer sees none of A's pairs, and addressing A's pair id resolves to 404.
        var listB = await client.SendAsync(Bearer(HttpMethod.Get, "/api/pairs", tokenB));
        using var docB = JsonDocument.Parse(await listB.Content.ReadAsStringAsync());
        docB.RootElement.GetArrayLength().Should().Be(0);

        var getCross = await client.SendAsync(Bearer(HttpMethod.Get, $"/api/pairs/{pairId}", tokenB));
        getCross.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Pairs_get_without_bearer_or_cookie_returns_401()
    {
        using var factory = Build();
        var client = NoRedirectClient(factory);

        (await client.GetAsync("/api/pairs")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/accounts")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
