using System;
using System.Collections.Concurrent;
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
using ZyncMaster.Server.Data;
using ZyncMaster.Server.Modules.Calendar;
using Xunit;

namespace ZyncMaster.Server.Tests.Calendar;

// Endpoint tests for Track A-2 (calendar-account connection). Exercised end-to-end through
// WebApplicationFactory so the wiring (Program.cs registration, RequireIdentityBearer, the
// signed-state round trip and the user-scoped store) is verified, not just handlers in
// isolation. A fake IMicrosoftTokenService captures the scopes the callback exchanges with and
// returns a deterministic refresh token + email; valid bearers come from the real
// IIdentityTokenService.
public class CalendarConnectEndpointsTests
{
    private const string CalendarStateCookieName = "sm_calendar_oauth_state";

    // Records the scopes ExchangeCalendarCodeAsync was last called with so a test can assert the
    // callback exchanged with the consented scope, and returns a fixed identity + refresh token.
    private sealed class FakeTokenService : IMicrosoftTokenService
    {
        public string? LastCalendarScopes { get; private set; }
        public string Email { get; init; } = "calendar@test";
        public string Refresh { get; init; } = "calendar-refresh-token";

        public Task<TokenResult> ExchangeCalendarCodeAsync(string code, string scopes, CancellationToken ct = default)
        {
            LastCalendarScopes = scopes;
            return Task.FromResult(new TokenResult
            {
                AccessToken = "calendar-access-token",
                RefreshToken = Refresh,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
                UserPrincipalName = Email,
                Subject = "calendar-oid",
                Email = Email,
                DisplayName = "Calendar Owner",
            });
        }

        public Task<TokenResult> ExchangeCodeAsync(string code, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TokenResult> ExchangeIdentityCodeAsync(string code, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TokenResult> RefreshAsync(string refreshToken, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static WebApplicationFactory<Program> CreateFactory(FakeTokenService fake) =>
        new ServerTestFactory().WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IMicrosoftTokenService>();
                s.AddSingleton<IMicrosoftTokenService>(fake);
            }));

    private static HttpClient NoRedirectClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    // Mints a real identity bearer for a fresh user and returns (bearer, userId).
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

    private static string ExtractCookie(HttpResponseMessage response, string name) =>
        response.Headers.GetValues("Set-Cookie")
            .First(c => c.StartsWith(name + "=", StringComparison.Ordinal))
            .Split(';')[0];

    private static string ExtractQueryValue(Uri location, string key)
    {
        var query = location.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (Uri.UnescapeDataString(pair[..idx]) == key)
                return Uri.UnescapeDataString(pair[(idx + 1)..]);
        }
        throw new InvalidOperationException($"{key} not found in {location}");
    }

    // ---- connect/graph: scopes, auth, validation -------------------------------------------

    [Fact]
    public async Task ConnectGraph_read_redirects_with_read_scopes_and_csrf_cookie()
    {
        using var factory = CreateFactory(new FakeTokenService());
        var (token, _) = IssueBearer(factory, "u-read", "u-read@test");
        var client = NoRedirectClient(factory);

        var resp = await client.SendAsync(
            Bearer(HttpMethod.Get, "/calendar/connect/graph?scope=read&port=51900&nonce=n1", token));

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = resp.Headers.Location!;
        location.ToString().Should().Contain("/authorize");
        location.ToString().Should().Contain("prompt=select_account");

        var scope = ExtractQueryValue(location, "scope");
        scope.Should().Contain("Calendars.Read");
        scope.Should().NotContain("Calendars.ReadWrite");
        scope.Should().Contain("offline_access");

        resp.Headers.GetValues("Set-Cookie")
            .Any(c => c.StartsWith(CalendarStateCookieName + "=", StringComparison.Ordinal))
            .Should().BeTrue();
    }

    [Fact]
    public async Task ConnectGraph_readwrite_redirects_with_readwrite_scopes()
    {
        using var factory = CreateFactory(new FakeTokenService());
        var (token, _) = IssueBearer(factory, "u-rw", "u-rw@test");
        var client = NoRedirectClient(factory);

        var resp = await client.SendAsync(
            Bearer(HttpMethod.Get, "/calendar/connect/graph?scope=readwrite&port=51901&nonce=n1", token));

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        ExtractQueryValue(resp.Headers.Location!, "scope").Should().Contain("Calendars.ReadWrite");
    }

    [Fact]
    public async Task ConnectGraph_without_bearer_returns_401()
    {
        using var factory = CreateFactory(new FakeTokenService());
        var client = NoRedirectClient(factory);

        var resp = await client.GetAsync("/calendar/connect/graph?scope=read&port=51902&nonce=n1");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("read", "80", "n")]        // port below 1024
    [InlineData("read", "70000", "n")]     // port above 65535
    [InlineData("read", "notanumber", "n")]
    [InlineData("read", "51903", "")]      // empty nonce
    [InlineData("bogus", "51903", "n")]    // invalid scope
    public async Task ConnectGraph_rejects_bad_scope_port_or_nonce(string scope, string port, string nonce)
    {
        using var factory = CreateFactory(new FakeTokenService());
        var (token, _) = IssueBearer(factory, $"u-bad-{scope}-{port}-{nonce}", "u-bad@test");
        var client = NoRedirectClient(factory);

        var resp = await client.SendAsync(Bearer(
            HttpMethod.Get,
            $"/calendar/connect/graph?scope={Uri.EscapeDataString(scope)}&port={Uri.EscapeDataString(port)}&nonce={Uri.EscapeDataString(nonce)}",
            token));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- connect/graph/start (JSON): the App's cookie-less entry point ----------------------

    [Fact]
    public async Task ConnectGraphStart_without_bearer_returns_401()
    {
        using var factory = CreateFactory(new FakeTokenService());
        var client = NoRedirectClient(factory);

        var req = WithBody(
            Bearer(HttpMethod.Post, "/api/calendar/connect/graph/start", null),
            new { scope = "read", port = 51960, nonce = "n1" });

        (await client.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ConnectGraphStart_returns_authorize_url_with_state_and_redirect_uri()
    {
        using var factory = CreateFactory(new FakeTokenService());
        var (token, _) = IssueBearer(factory, "start-ok", "start-ok@test");
        var client = NoRedirectClient(factory);

        var resp = await client.SendAsync(WithBody(
            Bearer(HttpMethod.Post, "/api/calendar/connect/graph/start", token),
            new { scope = "readwrite", port = 51961, nonce = "n1" }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var authorizeUrl = doc.RootElement.GetProperty("authorizeUrl").GetString();
        authorizeUrl.Should().NotBeNullOrEmpty();

        var uri = new Uri(authorizeUrl!);
        // Points at the Microsoft authority's /authorize endpoint.
        uri.Host.Should().Contain("microsoft");
        uri.AbsolutePath.Should().EndWith("/authorize");
        // Carries a non-empty signed state.
        ExtractQueryValue(uri, "state").Should().NotBeNullOrEmpty();
        // The redirect_uri mirrors the server's configured CalendarRedirectUri exactly (same URL
        // the GET flow uses), proving the App-facing JSON entry point and the redirect entry point
        // hand Microsoft the identical callback.
        ExtractQueryValue(uri, "redirect_uri").Should().Be(ConfiguredCalendarRedirectUri(factory));
        // read/write scope was requested.
        ExtractQueryValue(uri, "scope").Should().Contain("Calendars.ReadWrite");
    }

    [Fact]
    public async Task ConnectGraphStart_sets_csrf_cookie()
    {
        using var factory = CreateFactory(new FakeTokenService());
        var (token, _) = IssueBearer(factory, "start-cookie", "start-cookie@test");
        var client = NoRedirectClient(factory);

        var resp = await client.SendAsync(WithBody(
            Bearer(HttpMethod.Post, "/api/calendar/connect/graph/start", token),
            new { scope = "read", port = 51962, nonce = "n1" }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Headers.GetValues("Set-Cookie")
            .Any(c => c.StartsWith(CalendarStateCookieName + "=", StringComparison.Ordinal))
            .Should().BeTrue();
    }

    [Fact]
    public async Task ConnectGraphStart_empty_scope_defaults_to_readwrite()
    {
        using var factory = CreateFactory(new FakeTokenService());
        var (token, _) = IssueBearer(factory, "start-default", "start-default@test");
        var client = NoRedirectClient(factory);

        var resp = await client.SendAsync(WithBody(
            Bearer(HttpMethod.Post, "/api/calendar/connect/graph/start", token),
            new { port = 51963, nonce = "n1" }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var uri = new Uri(doc.RootElement.GetProperty("authorizeUrl").GetString()!);
        ExtractQueryValue(uri, "scope").Should().Contain("Calendars.ReadWrite");
    }

    [Theory]
    [InlineData("read", 80, "n")]        // port below 1024
    [InlineData("read", 70000, "n")]     // port above 65535
    [InlineData("read", 51964, "")]      // empty nonce
    [InlineData("bogus", 51964, "n")]    // invalid scope
    public async Task ConnectGraphStart_rejects_bad_scope_port_or_nonce(string scope, int port, string nonce)
    {
        using var factory = CreateFactory(new FakeTokenService());
        var (token, _) = IssueBearer(factory, $"start-bad-{scope}-{port}-{nonce}", "start-bad@test");
        var client = NoRedirectClient(factory);

        var resp = await client.SendAsync(WithBody(
            Bearer(HttpMethod.Post, "/api/calendar/connect/graph/start", token),
            new { scope, port, nonce }));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ConnectGraphStart_state_user_comes_from_token_not_body()
    {
        // The body smuggles a foreign userId; the endpoint must ignore it and pin the TOKEN's user
        // into the signed state. We unprotect the returned state with the host's own data protector
        // (same trick the upgrade-foreign-account test uses) and assert the userId is the token's.
        using var factory = CreateFactory(new FakeTokenService());
        var (token, tokenUserId) = IssueBearer(factory, "start-pin", "start-pin@test");
        var client = NoRedirectClient(factory);

        var resp = await client.SendAsync(WithBody(
            Bearer(HttpMethod.Post, "/api/calendar/connect/graph/start", token),
            new { scope = "read", port = 51965, nonce = "n1", userId = "attacker-user-id" }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var state = ExtractQueryValue(
            new Uri(doc.RootElement.GetProperty("authorizeUrl").GetString()!), "state");

        var stateUserId = UnprotectStateUserId(factory, state);
        stateUserId.Should().Be(tokenUserId);
        stateUserId.Should().NotBe("attacker-user-id");
    }

    [Fact]
    public async Task ConnectGraphStart_state_drives_callback_and_creates_account()
    {
        // End-to-end: the JSON state must be consumable by the shared callback exactly like the GET
        // flow's state, creating the account under the token's user.
        var fake = new FakeTokenService();
        using var factory = CreateFactory(fake);
        var (token, userId) = IssueBearer(factory, "start-e2e", "start-e2e@test");
        var client = NoRedirectClient(factory);

        var start = await client.SendAsync(WithBody(
            Bearer(HttpMethod.Post, "/api/calendar/connect/graph/start", token),
            new { scope = "readwrite", port = 51966, nonce = "e2e-nonce" }));
        start.StatusCode.Should().Be(HttpStatusCode.OK);
        var csrfCookie = ExtractCookie(start, CalendarStateCookieName);
        using var startDoc = JsonDocument.Parse(await start.Content.ReadAsStringAsync());
        var state = ExtractQueryValue(
            new Uri(startDoc.RootElement.GetProperty("authorizeUrl").GetString()!), "state");

        var callback = Bearer(HttpMethod.Get,
            $"/calendar/connect/callback/graph?code=abc&state={Uri.EscapeDataString(state)}", null);
        callback.Headers.Add("Cookie", csrfCookie);
        var cb = await client.SendAsync(callback);
        cb.StatusCode.Should().Be(HttpStatusCode.Redirect);
        cb.Headers.Location!.Port.Should().Be(51966);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        var row = db.CalendarAccounts.Single(a => a.UserId == userId);
        row.Scope.Should().Be(AccountScope.ReadWrite.ToString());
        row.Kind.Should().Be(AccountKind.Graph.ToString());
    }

    // ---- callback: creates the account under the state's user ------------------------------

    private static async Task<(string state, string csrfCookie)> StartConnect(
        HttpClient client, string token, string scope, int port, string nonce)
    {
        var connect = await client.SendAsync(Bearer(
            HttpMethod.Get,
            $"/calendar/connect/graph?scope={scope}&port={port}&nonce={nonce}",
            token));
        connect.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var state = ExtractQueryValue(connect.Headers.Location!, "state");
        var csrfCookie = ExtractCookie(connect, CalendarStateCookieName);
        return (state, csrfCookie);
    }

    [Fact]
    public async Task Callback_creates_account_with_correct_scope_under_state_user()
    {
        var fake = new FakeTokenService();
        using var factory = CreateFactory(fake);
        var (token, userId) = IssueBearer(factory, "cb-user", "cb-user@test");
        var client = NoRedirectClient(factory);

        var (state, csrfCookie) = await StartConnect(client, token, "readwrite", 51910, "cb-nonce");

        var callback = Bearer(
            HttpMethod.Get,
            $"/calendar/connect/callback/graph?code=abc&state={Uri.EscapeDataString(state)}",
            token: null);
        callback.Headers.Add("Cookie", csrfCookie);
        var resp = await client.SendAsync(callback);

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = resp.Headers.Location!;
        location.Scheme.Should().Be("http");
        location.Host.Should().Be("127.0.0.1");
        location.Port.Should().Be(51910);
        location.AbsolutePath.Should().Be("/calendar/callback");
        ExtractQueryValue(location, "status").Should().Be("connected");
        ExtractQueryValue(location, "nonce").Should().Be("cb-nonce");

        // The exchange used the read/write scopes.
        fake.LastCalendarScopes.Should().Contain("Calendars.ReadWrite");

        // The account was persisted under the state's user with the consented scope, and the
        // encrypted refresh token is recoverable but never surfaced.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        var row = db.CalendarAccounts.Single(a => a.UserId == userId);
        row.Kind.Should().Be(AccountKind.Graph.ToString());
        row.Provider.Should().Be("microsoft");
        row.AccountEmail.Should().Be("calendar@test");
        row.Scope.Should().Be(AccountScope.ReadWrite.ToString());
        row.Status.Should().Be("active");
        row.EncryptedRefreshToken.Should().NotBeNullOrEmpty();
        row.EncryptedRefreshToken.Should().NotBe("calendar-refresh-token");
    }

    [Fact]
    public async Task Callback_read_scope_creates_read_account()
    {
        var fake = new FakeTokenService();
        using var factory = CreateFactory(fake);
        var (token, userId) = IssueBearer(factory, "cb-read", "cb-read@test");
        var client = NoRedirectClient(factory);

        var (state, csrfCookie) = await StartConnect(client, token, "read", 51911, "rn");
        var callback = Bearer(HttpMethod.Get,
            $"/calendar/connect/callback/graph?code=abc&state={Uri.EscapeDataString(state)}", null);
        callback.Headers.Add("Cookie", csrfCookie);
        await client.SendAsync(callback);

        fake.LastCalendarScopes.Should().Contain("Calendars.Read");
        fake.LastCalendarScopes.Should().NotContain("Calendars.ReadWrite");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        db.CalendarAccounts.Single(a => a.UserId == userId).Scope.Should().Be(AccountScope.Read.ToString());
    }

    [Fact]
    public async Task Callback_with_tampered_state_returns_400()
    {
        using var factory = CreateFactory(new FakeTokenService());
        var (token, _) = IssueBearer(factory, "cb-tamper", "cb-tamper@test");
        var client = NoRedirectClient(factory);

        var (_, csrfCookie) = await StartConnect(client, token, "read", 51912, "n");
        var callback = Bearer(HttpMethod.Get,
            "/calendar/connect/callback/graph?code=abc&state=tampered-blob", null);
        callback.Headers.Add("Cookie", csrfCookie);

        (await client.SendAsync(callback)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Callback_with_mismatched_csrf_returns_400()
    {
        using var factory = CreateFactory(new FakeTokenService());
        var (token, _) = IssueBearer(factory, "cb-csrf", "cb-csrf@test");
        var client = NoRedirectClient(factory);

        var (stateA, _) = await StartConnect(client, token, "read", 51913, "a");
        var (_, csrfCookieB) = await StartConnect(client, token, "read", 51913, "b");

        var callback = Bearer(HttpMethod.Get,
            $"/calendar/connect/callback/graph?code=abc&state={Uri.EscapeDataString(stateA)}", null);
        callback.Headers.Add("Cookie", csrfCookieB);

        (await client.SendAsync(callback)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Callback_with_error_returns_html()
    {
        using var factory = CreateFactory(new FakeTokenService());
        var client = NoRedirectClient(factory);

        var resp = await client.GetAsync("/calendar/connect/callback/graph?error=access_denied");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
    }

    // ---- GET /api/calendar/accounts --------------------------------------------------------

    [Fact]
    public async Task ListAccounts_returns_only_callers_accounts_without_token()
    {
        var fake = new FakeTokenService();
        using var factory = CreateFactory(fake);
        var (tokenA, _) = IssueBearer(factory, "list-a", "list-a@test");
        var (tokenB, _) = IssueBearer(factory, "list-b", "list-b@test");
        var client = NoRedirectClient(factory);

        await ConnectOne(client, tokenA, "read", 51920, "na");

        var listA = await client.SendAsync(Bearer(HttpMethod.Get, "/api/calendar/accounts", tokenA));
        listA.StatusCode.Should().Be(HttpStatusCode.OK);
        var bodyA = await listA.Content.ReadAsStringAsync();
        bodyA.Should().Contain("calendar@test");
        bodyA.Should().NotContain("refreshToken");
        bodyA.Should().NotContain("calendar-refresh-token");

        // User B sees none of user A's accounts.
        var listB = await client.SendAsync(Bearer(HttpMethod.Get, "/api/calendar/accounts", tokenB));
        using var docB = JsonDocument.Parse(await listB.Content.ReadAsStringAsync());
        docB.RootElement.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task ListAccounts_without_bearer_returns_401()
    {
        using var factory = CreateFactory(new FakeTokenService());
        var client = NoRedirectClient(factory);

        (await client.GetAsync("/api/calendar/accounts")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- DELETE /api/calendar/accounts/{id} ------------------------------------------------

    [Fact]
    public async Task Delete_removes_account()
    {
        var fake = new FakeTokenService();
        using var factory = CreateFactory(fake);
        var (token, userId) = IssueBearer(factory, "del-user", "del-user@test");
        var client = NoRedirectClient(factory);

        await ConnectOne(client, token, "read", 51930, "n");
        var id = SingleAccountId(factory, userId);

        var del = await client.SendAsync(Bearer(HttpMethod.Delete, $"/api/calendar/accounts/{id}", token));
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        db.CalendarAccounts.Any(a => a.Id == id).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_cross_user_returns_404_and_keeps_account()
    {
        var fake = new FakeTokenService();
        using var factory = CreateFactory(fake);
        var (tokenA, userIdA) = IssueBearer(factory, "del-a", "del-a@test");
        var (tokenB, _) = IssueBearer(factory, "del-b", "del-b@test");
        var client = NoRedirectClient(factory);

        await ConnectOne(client, tokenA, "read", 51931, "n");
        var id = SingleAccountId(factory, userIdA);

        var del = await client.SendAsync(Bearer(HttpMethod.Delete, $"/api/calendar/accounts/{id}", tokenB));
        del.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        db.CalendarAccounts.Any(a => a.Id == id).Should().BeTrue();
    }

    // ---- upgrade-scope ---------------------------------------------------------------------

    [Fact]
    public async Task UpgradeScope_own_account_upgrades_read_to_readwrite()
    {
        var fake = new FakeTokenService { Refresh = "rotated-rw-token" };
        using var factory = CreateFactory(fake);
        var (token, userId) = IssueBearer(factory, "up-user", "up-user@test");
        var client = NoRedirectClient(factory);

        await ConnectOne(client, token, "read", 51940, "n");
        var id = SingleAccountId(factory, userId);

        // Start the upgrade; the response carries an authorize URL with a signed state.
        var start = await client.SendAsync(Bearer(
            HttpMethod.Post, $"/api/calendar/accounts/{id}/upgrade-scope?port=51940&nonce=upn", token));
        start.StatusCode.Should().Be(HttpStatusCode.OK);
        var csrfCookie = ExtractCookie(start, CalendarStateCookieName);
        using var startDoc = JsonDocument.Parse(await start.Content.ReadAsStringAsync());
        var authorizeUrl = new Uri(startDoc.RootElement.GetProperty("authorizeUrl").GetString()!);
        var state = ExtractQueryValue(authorizeUrl, "state");
        authorizeUrl.ToString().Should().Contain("prompt=consent");
        authorizeUrl.ToString().Should().Contain("Calendars.ReadWrite");

        // Drive the shared callback with the upgrade state.
        var callback = Bearer(HttpMethod.Get,
            $"/calendar/connect/callback/graph?code=abc&state={Uri.EscapeDataString(state)}", null);
        callback.Headers.Add("Cookie", csrfCookie);
        (await client.SendAsync(callback)).StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        var row = db.CalendarAccounts.Single(a => a.Id == id);
        row.Scope.Should().Be(AccountScope.ReadWrite.ToString());
        // Token rotated to the upgrade's refresh token (still encrypted at rest).
        row.EncryptedRefreshToken.Should().NotBe("rotated-rw-token");
    }

    [Fact]
    public async Task UpgradeScope_unknown_account_returns_404()
    {
        using var factory = CreateFactory(new FakeTokenService());
        var (token, _) = IssueBearer(factory, "up-unknown", "up-unknown@test");
        var client = NoRedirectClient(factory);

        var resp = await client.SendAsync(Bearer(
            HttpMethod.Post, "/api/calendar/accounts/does-not-exist/upgrade-scope?port=51941&nonce=n", token));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpgradeScope_callback_for_foreign_account_is_rejected()
    {
        // User A owns the account. We forge an upgrade state binding A's accountId to user B and
        // drive the callback — the handler must verify the account belongs to the STATE user (B)
        // and reject, leaving A's account untouched at read scope.
        var fake = new FakeTokenService();
        using var factory = CreateFactory(fake);
        var (tokenA, userIdA) = IssueBearer(factory, "xu-a", "xu-a@test");
        var (_, userIdB) = IssueBearer(factory, "xu-b", "xu-b@test");
        var client = NoRedirectClient(factory);

        await ConnectOne(client, tokenA, "read", 51942, "n");
        var accountIdOfA = SingleAccountId(factory, userIdA);

        // Forge a signed upgrade state for user B referencing A's account id, using the host's
        // own data protector so the blob unprotects (this models a user-B-initiated upgrade that
        // names someone else's account).
        var (state, csrf) = ForgeUpgradeState(factory, userIdB, accountIdOfA, 51942, "n");

        var callback = Bearer(HttpMethod.Get,
            $"/calendar/connect/callback/graph?code=abc&state={Uri.EscapeDataString(state)}", null);
        callback.Headers.Add("Cookie", $"{CalendarStateCookieName}={csrf}");
        var resp = await client.SendAsync(callback);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        db.CalendarAccounts.Single(a => a.Id == accountIdOfA).Scope.Should().Be(AccountScope.Read.ToString());
    }

    // ---- outlook-com -----------------------------------------------------------------------

    [Fact]
    public async Task OutlookCom_with_own_device_creates_com_account_without_token()
    {
        using var factory = CreateFactory(new FakeTokenService());
        var (token, userId) = IssueBearer(factory, "oc-user", "oc-user@test");
        var deviceId = SeedDevice(factory, userId, "Laptop");
        var client = NoRedirectClient(factory);

        var resp = await client.SendAsync(WithBody(
            Bearer(HttpMethod.Post, "/calendar/connect/outlook-com", token), new { deviceId }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        var row = db.CalendarAccounts.Single(a => a.UserId == userId);
        row.Kind.Should().Be(AccountKind.OutlookCom.ToString());
        row.Provider.Should().Be("outlook-com");
        row.DeviceId.Should().Be(deviceId);
        row.Scope.Should().Be(AccountScope.ReadWrite.ToString());
        row.EncryptedRefreshToken.Should().BeNull();
    }

    [Fact]
    public async Task OutlookCom_with_foreign_device_is_rejected()
    {
        using var factory = CreateFactory(new FakeTokenService());
        var (_, userIdA) = IssueBearer(factory, "oc-a", "oc-a@test");
        var (tokenB, _) = IssueBearer(factory, "oc-b", "oc-b@test");
        var foreignDeviceId = SeedDevice(factory, userIdA, "A-Laptop");
        var client = NoRedirectClient(factory);

        var resp = await client.SendAsync(WithBody(
            Bearer(HttpMethod.Post, "/calendar/connect/outlook-com", tokenB),
            new { deviceId = foreignDeviceId }));

        ((int)resp.StatusCode).Should().BeOneOf(400, 403);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        db.CalendarAccounts.Any().Should().BeFalse();
    }

    [Fact]
    public async Task OutlookCom_without_bearer_returns_401()
    {
        using var factory = CreateFactory(new FakeTokenService());
        var client = NoRedirectClient(factory);

        var req = WithBody(
            Bearer(HttpMethod.Post, "/calendar/connect/outlook-com", null), new { deviceId = "x" });

        (await client.SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- helpers ---------------------------------------------------------------------------

    // Runs connect/graph + callback so exactly one account ends up persisted for the caller.
    private static async Task ConnectOne(HttpClient client, string token, string scope, int port, string nonce)
    {
        var (state, csrfCookie) = await StartConnect(client, token, scope, port, nonce);
        var callback = Bearer(HttpMethod.Get,
            $"/calendar/connect/callback/graph?code=abc&state={Uri.EscapeDataString(state)}", null);
        callback.Headers.Add("Cookie", csrfCookie);
        (await client.SendAsync(callback)).StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    private static string SingleAccountId(WebApplicationFactory<Program> factory, string userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        return db.CalendarAccounts.Single(a => a.UserId == userId).Id;
    }

    private static string SeedDevice(WebApplicationFactory<Program> factory, string userId, string name)
    {
        var id = Guid.NewGuid().ToString("N");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        db.Devices.Add(new DeviceRow
        {
            Id = id,
            UserId = userId,
            Name = name,
            ApiKeyHash = "hash",
            CreatedUtc = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
        return id;
    }

    // Forges a signed calendar upgrade state via the host's IDataProtectionProvider so the blob
    // round-trips. Mirrors the (private) state shape in CalendarConnectEndpoints.
    private static (string state, string csrf) ForgeUpgradeState(
        WebApplicationFactory<Program> factory, string userId, string accountId, int port, string nonce)
    {
        var csrf = "forged-csrf-" + Guid.NewGuid().ToString("N");
        var payload = new
        {
            userId,
            scope = AccountScope.ReadWrite.ToString(),
            accountId,
            port,
            nonce,
            csrf,
        };
        using var scope = factory.Services.CreateScope();
        var dp = scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>();
        var protector = dp.CreateProtector("ZyncMaster.CalendarOAuthState");
        var bytes = protector.Protect(JsonSerializer.SerializeToUtf8Bytes(payload));
        var state = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (state, csrf);
    }

    private static HttpRequestMessage WithBody(HttpRequestMessage req, object body)
    {
        req.Content = JsonContent.Create(body);
        return req;
    }

    // The server's configured CalendarRedirectUri (read from the live host options) so a test can
    // assert the authorize URL hands Microsoft the exact callback the server expects.
    private static string ConfiguredCalendarRedirectUri(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var opts = scope.ServiceProvider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<ServerOptions>>();
        return opts.Value.CalendarRedirectUri;
    }

    // Unprotects a signed calendar state blob with the host's own data protector and returns its
    // userId, so a test can prove the userId was pinned from the token (not the request body).
    private static string UnprotectStateUserId(WebApplicationFactory<Program> factory, string state)
    {
        using var scope = factory.Services.CreateScope();
        var dp = scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>();
        var protector = dp.CreateProtector("ZyncMaster.CalendarOAuthState");
        var s = state.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        var json = protector.Unprotect(Convert.FromBase64String(s));
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("userId").GetString()!;
    }
}
