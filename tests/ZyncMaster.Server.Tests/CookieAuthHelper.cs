using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ZyncMaster.Server.Tests;

// Mints a real panel session cookie by driving the actual /connect → /connect/callback
// OAuth flow against a fake token service. The returned HttpClient persists cookies
// (HandleCookies = true) so the session cookie rides along on every subsequent request,
// exactly like a signed-in browser. Used by panel / pairs-management tests that are now
// cookie-gated instead of api-key-gated.
public static class CookieAuthHelper
{
    public sealed class FakeIdentityTokenService : IMicrosoftTokenService
    {
        public string Subject { get; set; } = "oid-123";
        public string Upn { get; set; } = "user@test";
        public string DisplayName { get; set; } = "Test User";
        public string RefreshToken { get; set; } = "rt";

        public Task<TokenResult> ExchangeCodeAsync(string code, CancellationToken ct = default) =>
            Task.FromResult(new TokenResult
            {
                AccessToken = "at",
                RefreshToken = RefreshToken,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
                UserPrincipalName = Upn,
                Subject = Subject,
                Email = Upn,
                DisplayName = DisplayName,
            });

        public Task<TokenResult> ExchangeIdentityCodeAsync(string code, CancellationToken ct = default) =>
            ExchangeCodeAsync(code, ct);

        public Task<TokenResult> RefreshAsync(string refreshToken, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    // Adds the fake identity token service to a factory so the OAuth callback produces a
    // known user. Chain this on top of any other ConfigureServices the test needs.
    public static WebApplicationFactory<Program> WithFakeIdentity(
        this WebApplicationFactory<Program> factory, FakeIdentityTokenService fake) =>
        factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IMicrosoftTokenService>();
            s.AddSingleton<IMicrosoftTokenService>(fake);
        }));

    // Drives the real sign-in flow and returns a cookie-bearing client.
    public static async Task<HttpClient> SignInAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

        var connect = await client.GetAsync("/connect");
        var state = ExtractState(connect.Headers.Location!);

        // The state cookie is already held by the client (HandleCookies = true); just send
        // the matching state back in the callback query.
        var callback = await client.GetAsync(
            $"/connect/callback?code=abc&state={Uri.EscapeDataString(state)}");
        callback.EnsureSuccessStatusCode2();

        return client;
    }

    private static void EnsureSuccessStatusCode2(this HttpResponseMessage resp)
    {
        // The callback ends in a redirect (302); treat that as success here.
        if ((int)resp.StatusCode is < 200 or >= 400)
            throw new InvalidOperationException($"Sign-in callback failed: {(int)resp.StatusCode}");
    }

    private static string ExtractState(Uri location)
    {
        var query = location.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (Uri.UnescapeDataString(pair[..idx]) == "state")
                return Uri.UnescapeDataString(pair[(idx + 1)..]);
        }
        throw new InvalidOperationException("state not found in Location");
    }
}
