using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace ZyncMaster.Server.Tests.Identity;

public class ConnectEndpointsTests
{
    private sealed class FakeTokenService : IMicrosoftTokenService
    {
        public string? Subject { get; init; }
        public string Upn { get; init; } = "user@test";
        public string? Name { get; init; }

        public Task<TokenResult> ExchangeCodeAsync(string code, CancellationToken ct = default) =>
            Task.FromResult(new TokenResult
            {
                AccessToken = "at",
                RefreshToken = "rt",
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
                UserPrincipalName = Upn,
                Subject = Subject,
                Email = Upn,
                DisplayName = Name,
            });

        public Task<TokenResult> ExchangeIdentityCodeAsync(string code, CancellationToken ct = default) =>
            ExchangeCodeAsync(code, ct);

        public Task<TokenResult> ExchangeCalendarCodeAsync(
            string code, string scopes, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TokenResult> RefreshAsync(string refreshToken, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static WebApplicationFactory<Program> CreateFactory() =>
        new ServerTestFactory().WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IMicrosoftTokenService>();
                s.AddSingleton<IMicrosoftTokenService>(new FakeTokenService());
            }));

    private static HttpClient NoRedirectClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    // Extracts the bare cookie value ("name=value") from a Set-Cookie header.
    private static string ExtractStateCookie(HttpResponseMessage response)
    {
        var setCookie = response.Headers.GetValues("Set-Cookie")
            .First(c => c.StartsWith("sm_oauth_state=", StringComparison.Ordinal));
        return setCookie.Split(';')[0];
    }

    private static string ExtractStateFromLocation(Uri location)
    {
        var query = location.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (Uri.UnescapeDataString(pair.Substring(0, idx)) == "state")
                return Uri.UnescapeDataString(pair.Substring(idx + 1));
        }
        throw new InvalidOperationException("state not found in Location");
    }

    [Fact]
    public async Task Connect_redirects_to_authorize_with_state_cookie()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        var resp = await client.GetAsync("/connect");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = resp.Headers.Location!.ToString();
        location.Should().Contain("/authorize");
        location.Should().Contain("client_id");
        location.Should().Contain("redirect_uri");
        location.Should().Contain("response_type=code");
        location.Should().Contain("scope");
        location.Should().Contain("state=");
        // Multi-account: forces Entra to show the account chooser every time instead of
        // silently reusing the first signed-in session.
        location.Should().Contain("prompt=select_account");

        resp.Headers.Contains("Set-Cookie").Should().BeTrue();
        resp.Headers.GetValues("Set-Cookie")
            .Any(c => c.StartsWith("sm_oauth_state=", StringComparison.Ordinal))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Callback_happy_path_upserts_user_sets_cookie_stores_account_and_redirects_home()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        var connectResp = await client.GetAsync("/connect");
        var state = ExtractStateFromLocation(connectResp.Headers.Location!);
        var cookie = ExtractStateCookie(connectResp);

        var callback = new HttpRequestMessage(
            HttpMethod.Get, $"/connect/callback?code=abc&state={Uri.EscapeDataString(state)}");
        callback.Headers.Add("Cookie", cookie);

        var resp = await client.SendAsync(callback);

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().Be("/");

        // The session cookie was issued.
        resp.Headers.GetValues("Set-Cookie")
            .Any(c => c.StartsWith("sm_session=", StringComparison.Ordinal))
            .Should().BeTrue();

        // The user was upserted (subject fell back to the UPN) and the account persisted
        // under that user — verified against the DB directly since HasAnyAsync() outside a
        // request scopes to the seeded "default" user, not the just-created one.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMaster.Server.Data.ZyncMasterDbContext>();
        var user = db.Users.FirstOrDefault(u => u.Provider == "microsoft" && u.Subject == "user@test");
        user.Should().NotBeNull();
        user!.Email.Should().Be("user@test");
        db.ConnectedAccounts.Any(a => a.UserId == user.Id).Should().BeTrue();
    }

    [Fact]
    public async Task Callback_honors_returnTo_redirect()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        // returnTo is stashed at /connect and consumed at callback.
        var connectResp = await client.GetAsync("/connect?returnTo=%2Fpair%3Fcode%3Dxyz");
        var state = ExtractStateFromLocation(connectResp.Headers.Location!);
        var stateCookie = ExtractStateCookie(connectResp);
        var returnToCookie = connectResp.Headers.GetValues("Set-Cookie")
            .First(c => c.StartsWith("sm_oauth_returnto=", StringComparison.Ordinal)).Split(';')[0];

        var callback = new HttpRequestMessage(
            HttpMethod.Get, $"/connect/callback?code=abc&state={Uri.EscapeDataString(state)}");
        callback.Headers.Add("Cookie", stateCookie);
        callback.Headers.Add("Cookie", returnToCookie);

        var resp = await client.SendAsync(callback);

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().Be("/pair?code=xyz");
    }

    [Fact]
    public async Task Callback_ignores_offsite_returnTo()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        var connectResp = await client.GetAsync("/connect?returnTo=https%3A%2F%2Fevil.example%2Fx");
        var state = ExtractStateFromLocation(connectResp.Headers.Location!);
        var stateCookie = ExtractStateCookie(connectResp);
        var returnToCookie = connectResp.Headers.GetValues("Set-Cookie")
            .First(c => c.StartsWith("sm_oauth_returnto=", StringComparison.Ordinal)).Split(';')[0];

        var callback = new HttpRequestMessage(
            HttpMethod.Get, $"/connect/callback?code=abc&state={Uri.EscapeDataString(state)}");
        callback.Headers.Add("Cookie", stateCookie);
        callback.Headers.Add("Cookie", returnToCookie);

        var resp = await client.SendAsync(callback);

        resp.Headers.Location!.ToString().Should().Be("/");
    }

    [Fact]
    public async Task Callback_with_error_returns_html_and_stores_nothing()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        var resp = await client.GetAsync("/connect/callback?error=access_denied");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/html");

        var store = factory.Services.GetRequiredService<IConnectedAccountStore>();
        (await store.HasAnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Callback_with_wrong_state_returns_400()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        var connectResp = await client.GetAsync("/connect");
        var cookie = ExtractStateCookie(connectResp);

        var callback = new HttpRequestMessage(
            HttpMethod.Get, "/connect/callback?code=abc&state=not-the-right-state");
        callback.Headers.Add("Cookie", cookie);

        var resp = await client.SendAsync(callback);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // Drives the real /connect -> /connect/callback flow and returns nothing; the user is created
    // as a side effect. Shared by the unification tests below.
    private static async Task SignInThroughPanelAsync(WebApplicationFactory<Program> factory)
    {
        var client = NoRedirectClient(factory);
        var connectResp = await client.GetAsync("/connect");
        var state = ExtractStateFromLocation(connectResp.Headers.Location!);
        var cookie = ExtractStateCookie(connectResp);

        var callback = new HttpRequestMessage(
            HttpMethod.Get, $"/connect/callback?code=abc&state={Uri.EscapeDataString(state)}");
        callback.Headers.Add("Cookie", cookie);
        var resp = await client.SendAsync(callback);
        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    // FIX B — the panel sign-in door (/connect/callback) now routes through the unified
    // UpsertByLoginAsync, so it writes an IdentityLoginRow (the legacy UpsertAsync never did). This
    // is what makes every entry door converge on one IdentityLogins-keyed identity model.
    [Fact]
    public async Task Panel_callback_writes_an_identity_login_row()
    {
        using var factory = CreateFactory();

        await SignInThroughPanelAsync(factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMaster.Server.Data.ZyncMasterDbContext>();
        // Subject fell back to the UPN ("user@test"); the unified path stores provider/email lowercased.
        var login = db.IdentityLogins.SingleOrDefault(
            l => l.Provider == "microsoft" && l.ProviderSubject == "user@test");
        login.Should().NotBeNull("the panel callback must record the login in IdentityLogins");
        login!.EmailVerified.Should().BeFalse("Microsoft's email claim is not proof-of-possession");
    }

    // FIX B — the panel door and the modern sign-in door resolve to the SAME UserRow. The modern
    // door (identity OAuth + magic-link) uses UpsertByLoginAsync directly; the panel callback now
    // does too, so the same (provider, subject) cannot fork into two users. Previously the panel's
    // UpsertAsync and the modern UpsertByLoginAsync could create two distinct UserRows for the same
    // person, splitting their pairs/accounts/devices.
    [Fact]
    public async Task Panel_and_modern_door_resolve_to_the_same_user()
    {
        using var factory = CreateFactory();

        // 1. Sign in through the panel door.
        await SignInThroughPanelAsync(factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMaster.Server.Data.ZyncMasterDbContext>();
        var panelUser = db.Users.Single(u => u.Provider == "microsoft" && u.Subject == "user@test");

        // 2. The modern door for the SAME identity (same provider+subject) must return that user,
        //    not mint a second one. UpsertByLoginAsync is exactly what both the modern Microsoft
        //    callback and the magic-link callback call.
        var users = factory.Services.GetRequiredService<IUserStore>();
        var modernUser = await users.UpsertByLoginAsync(
            "microsoft", "user@test", "user@test", emailVerified: false, "Test User");

        modernUser.Id.Should().Be(panelUser.Id);

        // Exactly one canonical user + one login for this identity — no fork.
        db.ChangeTracker.Clear();
        db.Users.Count(u => u.Provider == "microsoft" && u.Subject == "user@test").Should().Be(1);
        db.IdentityLogins.Count(l => l.Provider == "microsoft" && l.ProviderSubject == "user@test").Should().Be(1);
    }
}
