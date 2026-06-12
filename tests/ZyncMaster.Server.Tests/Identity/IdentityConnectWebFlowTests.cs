using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace ZyncMaster.Server.Tests.Identity;

// Web mode of the Microsoft identity OAuth flow (the Angular SPA has no loopback listener):
// `mode=web` on /identity/connect/microsoft makes the callback land the one-time handle on the
// FIXED same-origin SPA path /zync-web/auth/callback?handle=&nonce= instead of
// http://127.0.0.1:{port}. The optional returnTo is validated against a strict allow-list (only
// the SPA callback, relative or PublicBaseUrl-pinned) and never echoed into the redirect. State
// signing, csrf double-submit, handle redeem and refresh are the existing mechanisms, unchanged.
public class IdentityConnectWebFlowTests
{
    private const string IdentityStateCookieName = "sm_identity_oauth_state";
    private const string SpaCallbackPath = "/zync-web/auth/callback";

    // Fake token service whose ExchangeIdentityCodeAsync returns a fixed identity, mirroring
    // IdentityConnectEndpointsTests. The legacy exchanges are not exercised by this flow.
    private sealed class FakeTokenService : IMicrosoftTokenService
    {
        public Task<TokenResult> ExchangeIdentityCodeAsync(string code, CancellationToken ct = default) =>
            Task.FromResult(new TokenResult
            {
                AccessToken = "calendar-at-should-not-be-used",
                RefreshToken = "calendar-rt-should-not-be-stored",
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
                UserPrincipalName = "web@test",
                Subject = "oid-web",
                Email = "web@test",
                DisplayName = "Web Tester",
            });

        public Task<TokenResult> ExchangeCodeAsync(string code, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TokenResult> ExchangeCalendarCodeAsync(
            string code, string scopes, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TokenResult> RefreshAsync(string refreshToken, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static WebApplicationFactory<Program> CreateFactory(string? publicBaseUrl = null) =>
        new ServerTestFactory().WithWebHostBuilder(b =>
        {
            if (publicBaseUrl is not null)
                b.UseSetting("Server:PublicBaseUrl", publicBaseUrl);
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IMicrosoftTokenService>();
                s.AddSingleton<IMicrosoftTokenService>(new FakeTokenService());
            });
        });

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

    // Runs start (web mode) + callback and returns the callback response.
    private static async Task<HttpResponseMessage> RunWebFlowAsync(
        HttpClient client, string nonce, string start = "/identity/connect/microsoft?mode=web")
    {
        var connect = await client.GetAsync($"{start}&nonce={Uri.EscapeDataString(nonce)}");
        connect.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var state = ExtractQueryValue(connect.Headers.Location!, "state");
        var csrfCookie = ExtractCookie(connect, IdentityStateCookieName);

        var callback = new HttpRequestMessage(
            HttpMethod.Get,
            $"/identity/connect/callback/microsoft?code=abc&state={Uri.EscapeDataString(state)}");
        callback.Headers.Add("Cookie", csrfCookie);
        return await client.SendAsync(callback);
    }

    [Fact]
    public async Task Web_start_without_port_redirects_to_authorize_with_state_and_csrf_cookie()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        var resp = await client.GetAsync("/identity/connect/microsoft?mode=web&nonce=web-n-1");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = resp.Headers.Location!.ToString();
        location.Should().Contain("/authorize");
        location.Should().Contain("response_type=code");
        location.Should().Contain("state=");

        // Identity scopes only, exactly like the desktop flow.
        var scope = ExtractQueryValue(resp.Headers.Location!, "scope");
        scope.Should().Contain("openid");
        scope.Should().NotContain("Calendars");

        resp.Headers.GetValues("Set-Cookie")
            .Any(c => c.StartsWith(IdentityStateCookieName + "=", StringComparison.Ordinal))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Web_start_still_requires_a_nonce()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        var resp = await client.GetAsync("/identity/connect/microsoft?mode=web");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Non_web_start_still_requires_a_loopback_port()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        var resp = await client.GetAsync("/identity/connect/microsoft?nonce=n-1"); // no port, no mode

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("https://evil.example/zync-web/auth/callback")]
    [InlineData("//evil.example/zync-web/auth/callback")]
    [InlineData("/zync-web/auth/callback/../../steal")]
    [InlineData("/somewhere-else")]
    [InlineData("https://app.devlabperu.com.evil.example/zync-web/auth/callback")]
    public async Task Web_start_rejects_a_returnTo_outside_the_allow_list(string returnTo)
    {
        using var factory = CreateFactory(publicBaseUrl: "https://app.devlabperu.com");
        var client = NoRedirectClient(factory);

        var resp = await client.GetAsync(
            $"/identity/connect/microsoft?mode=web&nonce=n-1&returnTo={Uri.EscapeDataString(returnTo)}");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("/zync-web/auth/callback")]
    [InlineData("https://app.devlabperu.com/zync-web/auth/callback")]
    public async Task Web_start_accepts_an_allow_listed_returnTo(string returnTo)
    {
        using var factory = CreateFactory(publicBaseUrl: "https://app.devlabperu.com/");
        var client = NoRedirectClient(factory);

        var resp = await client.GetAsync(
            $"/identity/connect/microsoft?mode=web&nonce=n-1&returnTo={Uri.EscapeDataString(returnTo)}");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().Contain("/authorize");
    }

    [Fact]
    public async Task Web_start_without_configured_PublicBaseUrl_rejects_an_absolute_returnTo()
    {
        using var factory = CreateFactory(); // PublicBaseUrl empty (test host default)
        var client = NoRedirectClient(factory);

        var resp = await client.GetAsync(
            "/identity/connect/microsoft?mode=web&nonce=n-1&returnTo=" +
            Uri.EscapeDataString("https://app.devlabperu.com/zync-web/auth/callback"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Web_callback_redirects_to_the_fixed_spa_path_with_handle_and_nonce()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        var resp = await RunWebFlowAsync(client, "web-n-42");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = resp.Headers.Location!;
        location.IsAbsoluteUri.Should().BeFalse(); // same-origin, never an external host
        location.OriginalString.Should().StartWith(SpaCallbackPath + "?");
        var parsed = new Uri("http://x" + location.OriginalString);
        ExtractQueryValue(parsed, "nonce").Should().Be("web-n-42");
        ExtractQueryValue(parsed, "handle").Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Web_callback_redirect_ignores_the_returnTo_and_uses_the_constant_path()
    {
        using var factory = CreateFactory(publicBaseUrl: "https://app.devlabperu.com");
        var client = NoRedirectClient(factory);

        var resp = await RunWebFlowAsync(
            client, "n-7",
            "/identity/connect/microsoft?mode=web&returnTo=" +
            Uri.EscapeDataString("https://app.devlabperu.com/zync-web/auth/callback"));

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        // Even with an (allow-listed) absolute returnTo the redirect is the fixed relative path.
        resp.Headers.Location!.OriginalString.Should().StartWith(SpaCallbackPath + "?");
    }

    [Fact]
    public async Task Web_handle_redeems_into_a_working_identity_bearer()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        var callback = await RunWebFlowAsync(client, "n-9");
        var handle = ExtractQueryValue(
            new Uri("http://x" + callback.Headers.Location!.OriginalString), "handle");

        var redeem = await client.PostAsJsonAsync("/identity/handle/redeem", new { handle });
        redeem.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await redeem.Content.ReadAsStringAsync());
        var access = doc.RootElement.GetProperty("accessToken").GetString();
        access.Should().NotBeNullOrEmpty();

        using var me = new HttpRequestMessage(HttpMethod.Get, "/api/identity/me");
        me.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access);
        var meResp = await client.SendAsync(me);
        meResp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await meResp.Content.ReadAsStringAsync()).Should().Contain("web@test");

        // One-time: a second redeem of the same handle fails.
        var second = await client.PostAsJsonAsync("/identity/handle/redeem", new { handle });
        ((int)second.StatusCode).Should().BeOneOf(400, 410);
    }

    [Fact]
    public async Task Web_callback_with_mismatched_csrf_cookie_returns_400()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        var connectA = await client.GetAsync("/identity/connect/microsoft?mode=web&nonce=a");
        var stateA = ExtractQueryValue(connectA.Headers.Location!, "state");

        var connectB = await client.GetAsync("/identity/connect/microsoft?mode=web&nonce=b");
        var csrfCookieB = ExtractCookie(connectB, IdentityStateCookieName);

        var callback = new HttpRequestMessage(
            HttpMethod.Get,
            $"/identity/connect/callback/microsoft?code=abc&state={Uri.EscapeDataString(stateA)}");
        callback.Headers.Add("Cookie", csrfCookieB);

        var resp = await client.SendAsync(callback);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Desktop_flow_still_redirects_to_the_loopback()
    {
        using var factory = CreateFactory();
        var client = NoRedirectClient(factory);

        var connect = await client.GetAsync("/identity/connect/microsoft?port=51795&nonce=app-n");
        var state = ExtractQueryValue(connect.Headers.Location!, "state");
        var csrfCookie = ExtractCookie(connect, IdentityStateCookieName);

        var callback = new HttpRequestMessage(
            HttpMethod.Get,
            $"/identity/connect/callback/microsoft?code=abc&state={Uri.EscapeDataString(state)}");
        callback.Headers.Add("Cookie", csrfCookie);
        var resp = await client.SendAsync(callback);

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = resp.Headers.Location!;
        location.Host.Should().Be("127.0.0.1");
        location.Port.Should().Be(51795);
        location.AbsolutePath.Should().Be("/identity/callback");
    }
}
