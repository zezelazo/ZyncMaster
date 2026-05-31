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
using Xunit;

namespace ZyncMaster.Server.Tests.Identity;

public class IdentityConnectEndpointsTests
{
    private const string IdentityStateCookieName = "sm_identity_oauth_state";

    // Fake token service whose ExchangeIdentityCodeAsync returns a fixed identity. The legacy
    // ExchangeCodeAsync/RefreshAsync are not exercised by the identity flow.
    private sealed class FakeTokenService : IMicrosoftTokenService
    {
        public string? Subject { get; init; } = "oid-id";
        public string Upn { get; init; } = "id@test";
        public string? Name { get; init; } = "Identity Tester";

        public Task<TokenResult> ExchangeIdentityCodeAsync(string code, CancellationToken ct = default) =>
            Task.FromResult(new TokenResult
            {
                AccessToken = "calendar-at-should-not-be-used",
                RefreshToken = "calendar-rt-should-not-be-stored",
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
                UserPrincipalName = Upn,
                Subject = Subject,
                Email = Upn,
                DisplayName = Name,
            });

        public Task<TokenResult> ExchangeCodeAsync(string code, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TokenResult> ExchangeCalendarCodeAsync(
            string code, string scopes, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TokenResult> RefreshAsync(string refreshToken, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static WebApplicationFactory<Program> CreateFactory(FakeTokenService? fake = null) =>
        new ServerTestFactory().WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IMicrosoftTokenService>();
                s.AddSingleton<IMicrosoftTokenService>(fake ?? new FakeTokenService());
            }));

    private static HttpClient NoRedirectClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static string ExtractCookie(HttpResponseMessage response, string name)
    {
        var setCookie = response.Headers.GetValues("Set-Cookie")
            .First(c => c.StartsWith(name + "=", StringComparison.Ordinal));
        return setCookie.Split(';')[0];
    }

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

    [Fact]
    public async Task Connect_redirects_to_authorize_with_identity_scopes_state_and_csrf_cookie()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        var resp = await client.GetAsync("/identity/connect/microsoft?port=51789&nonce=app-nonce-1");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = resp.Headers.Location!.ToString();
        location.Should().Contain("/authorize");
        location.Should().Contain("response_type=code");
        location.Should().Contain("prompt=select_account");
        location.Should().Contain("state=");

        // Identity scopes (openid email profile), NOT calendar scopes.
        var scope = ExtractQueryValue(resp.Headers.Location!, "scope");
        scope.Should().Contain("openid");
        scope.Should().Contain("email");
        scope.Should().Contain("profile");
        scope.Should().NotContain("Calendars");

        // CSRF cookie set.
        resp.Headers.GetValues("Set-Cookie")
            .Any(c => c.StartsWith(IdentityStateCookieName + "=", StringComparison.Ordinal))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("80", "app-nonce")]      // port below 1024
    [InlineData("70000", "app-nonce")]   // port above 65535
    [InlineData("notanumber", "app-nonce")]
    [InlineData("51789", "")]            // empty nonce
    public async Task Connect_rejects_bad_port_or_nonce(string port, string nonce)
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        var resp = await client.GetAsync(
            $"/identity/connect/microsoft?port={Uri.EscapeDataString(port)}&nonce={Uri.EscapeDataString(nonce)}");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Callback_happy_path_upserts_user_unverified_and_redirects_to_loopback_with_handle()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        var connect = await client.GetAsync("/identity/connect/microsoft?port=51790&nonce=nonce-xyz");
        var state = ExtractQueryValue(connect.Headers.Location!, "state");
        var csrfCookie = ExtractCookie(connect, IdentityStateCookieName);

        var callback = new HttpRequestMessage(
            HttpMethod.Get,
            $"/identity/connect/callback/microsoft?code=abc&state={Uri.EscapeDataString(state)}");
        callback.Headers.Add("Cookie", csrfCookie);

        var resp = await client.SendAsync(callback);

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = resp.Headers.Location!;
        location.Scheme.Should().Be("http");
        location.Host.Should().Be("127.0.0.1");
        location.Port.Should().Be(51790);
        location.AbsolutePath.Should().Be("/identity/callback");
        ExtractQueryValue(location, "nonce").Should().Be("nonce-xyz");
        var handle = ExtractQueryValue(location, "handle");
        handle.Should().NotBeNullOrEmpty();

        // The user was upserted from the id_token identity.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMaster.Server.Data.ZyncMasterDbContext>();
        db.Users.Any(u => u.Provider == "microsoft" && u.Subject == "oid-id").Should().BeTrue();

        // NO calendar account was connected by this identity flow.
        db.ConnectedAccounts.Any().Should().BeFalse();
    }

    [Fact]
    public async Task Callback_with_tampered_state_returns_400()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        var connect = await client.GetAsync("/identity/connect/microsoft?port=51791&nonce=n");
        var csrfCookie = ExtractCookie(connect, IdentityStateCookieName);

        var callback = new HttpRequestMessage(
            HttpMethod.Get, "/identity/connect/callback/microsoft?code=abc&state=tampered-blob");
        callback.Headers.Add("Cookie", csrfCookie);

        var resp = await client.SendAsync(callback);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Callback_with_mismatched_csrf_cookie_returns_400()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        // Two independent connect calls produce two different csrf values; pairing the state
        // of one with the cookie of the other must be rejected.
        var connectA = await client.GetAsync("/identity/connect/microsoft?port=51792&nonce=a");
        var stateA = ExtractQueryValue(connectA.Headers.Location!, "state");

        var connectB = await client.GetAsync("/identity/connect/microsoft?port=51792&nonce=b");
        var csrfCookieB = ExtractCookie(connectB, IdentityStateCookieName);

        var callback = new HttpRequestMessage(
            HttpMethod.Get,
            $"/identity/connect/callback/microsoft?code=abc&state={Uri.EscapeDataString(stateA)}");
        callback.Headers.Add("Cookie", csrfCookieB);

        var resp = await client.SendAsync(callback);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Callback_with_error_returns_html()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        var resp = await client.GetAsync(
            "/identity/connect/callback/microsoft?error=access_denied");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
    }

    [Fact]
    public async Task Redeem_returns_tokens_then_is_single_use()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        var connect = await client.GetAsync("/identity/connect/microsoft?port=51793&nonce=rdm");
        var state = ExtractQueryValue(connect.Headers.Location!, "state");
        var csrfCookie = ExtractCookie(connect, IdentityStateCookieName);

        var callbackReq = new HttpRequestMessage(
            HttpMethod.Get,
            $"/identity/connect/callback/microsoft?code=abc&state={Uri.EscapeDataString(state)}");
        callbackReq.Headers.Add("Cookie", csrfCookie);
        var callback = await client.SendAsync(callbackReq);
        var handle = ExtractQueryValue(callback.Headers.Location!, "handle");

        var first = await client.PostAsJsonAsync("/identity/handle/redeem", new { handle });
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var accessToken = doc.RootElement.GetProperty("accessToken").GetString();
        var refreshToken = doc.RootElement.GetProperty("refreshToken").GetString();
        accessToken.Should().NotBeNullOrEmpty();
        refreshToken.Should().NotBeNullOrEmpty();

        // The access token validates against the identity token service.
        var tokens = factory.Services.GetRequiredService<IIdentityTokenService>();
        tokens.ValidateAccessToken(accessToken!).Should().NotBeNull();

        // Second redeem of the same handle fails (one-time).
        var second = await client.PostAsJsonAsync("/identity/handle/redeem", new { handle });
        ((int)second.StatusCode).Should().BeOneOf(400, 410);
    }

    [Fact]
    public async Task Redeem_unknown_handle_fails()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        var resp = await client.PostAsJsonAsync(
            "/identity/handle/redeem", new { handle = "does-not-exist" });

        ((int)resp.StatusCode).Should().BeOneOf(400, 410);
    }

    [Fact]
    public async Task Me_with_valid_bearer_returns_profile()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        var connect = await client.GetAsync("/identity/connect/microsoft?port=51794&nonce=me");
        var state = ExtractQueryValue(connect.Headers.Location!, "state");
        var csrfCookie = ExtractCookie(connect, IdentityStateCookieName);

        var callbackReq = new HttpRequestMessage(
            HttpMethod.Get,
            $"/identity/connect/callback/microsoft?code=abc&state={Uri.EscapeDataString(state)}");
        callbackReq.Headers.Add("Cookie", csrfCookie);
        var callback = await client.SendAsync(callbackReq);
        var handle = ExtractQueryValue(callback.Headers.Location!, "handle");

        var redeem = await client.PostAsJsonAsync("/identity/handle/redeem", new { handle });
        using var redeemDoc = JsonDocument.Parse(await redeem.Content.ReadAsStringAsync());
        var accessToken = redeemDoc.RootElement.GetProperty("accessToken").GetString();

        var meReq = new HttpRequestMessage(HttpMethod.Get, "/api/identity/me");
        meReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var me = await client.SendAsync(meReq);

        me.StatusCode.Should().Be(HttpStatusCode.OK);
        using var meDoc = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        meDoc.RootElement.GetProperty("email").GetString().Should().Be("id@test");
        meDoc.RootElement.GetProperty("displayName").GetString().Should().Be("Identity Tester");
        meDoc.RootElement.TryGetProperty("userId", out _).Should().BeTrue();
        meDoc.RootElement.TryGetProperty("plan", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Me_without_bearer_returns_401()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        var resp = await client.GetAsync("/api/identity/me");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_with_invalid_bearer_returns_401()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        var meReq = new HttpRequestMessage(HttpMethod.Get, "/api/identity/me");
        meReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "garbage-token");
        var resp = await client.SendAsync(meReq);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
