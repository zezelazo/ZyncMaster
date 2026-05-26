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

namespace SyncMaster.Server.Tests.Identity;

public class ConnectEndpointsTests
{
    private sealed class FakeTokenService : IMicrosoftTokenService
    {
        public Task<TokenResult> ExchangeCodeAsync(string code, CancellationToken ct = default) =>
            Task.FromResult(new TokenResult
            {
                AccessToken = "at",
                RefreshToken = "rt",
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1),
                UserPrincipalName = "user@test",
            });

        public Task<TokenResult> RefreshAsync(string refreshToken, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
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

        resp.Headers.Contains("Set-Cookie").Should().BeTrue();
        resp.Headers.GetValues("Set-Cookie")
            .Any(c => c.StartsWith("sm_oauth_state=", StringComparison.Ordinal))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Callback_happy_path_stores_account_and_redirects_home()
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

        var store = factory.Services.GetRequiredService<IConnectedAccountStore>();
        (await store.HasAnyAsync()).Should().BeTrue();
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
}
