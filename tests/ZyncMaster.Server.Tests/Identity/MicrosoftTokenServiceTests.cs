using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Options;
using ZyncMaster.Graph;
using Xunit;

namespace ZyncMaster.Server.Tests.Identity;

public class MicrosoftTokenServiceTests
{
    private sealed class FakeSecretProvider : ISecretProvider
    {
        public string GetMicrosoftClientSecret() => "test-secret";
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public CapturingHandler(HttpResponseMessage response) => _response = response;

        public string? CapturedBody { get; private set; }
        public Uri? CapturedUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedUri = request.RequestUri;
            CapturedBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return _response;
        }
    }

    private static IOptions<ServerOptions> Options() =>
        Microsoft.Extensions.Options.Options.Create(new ServerOptions
        {
            Authority = "https://login.test/oauth2/v2.0",
            RedirectUri = "https://app/cb",
            MicrosoftClientId = "cid",
            Scopes = "offline_access Calendars.ReadWrite User.Read",
        });

    private static MicrosoftTokenService BuildService(CapturingHandler handler) =>
        new(new HttpClient(handler), Options(), new FakeSecretProvider());

    private static Dictionary<string, string> ParseForm(string? body)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(body))
            return result;

        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            // FormUrlEncodedContent encodes spaces as '+'; normalize before unescaping.
            var key = Uri.UnescapeDataString(pair.Substring(0, idx).Replace('+', ' '));
            var value = Uri.UnescapeDataString(pair.Substring(idx + 1).Replace('+', ' '));
            result[key] = value;
        }
        return result;
    }

    [Fact]
    public async Task ExchangeCode_posts_authorization_code_grant_and_parses_tokens()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"access_token\":\"AT\",\"refresh_token\":\"RT\",\"expires_in\":3600}"),
        });
        var service = BuildService(handler);
        var before = DateTimeOffset.UtcNow;

        var result = await service.ExchangeCodeAsync("the-code");

        handler.CapturedUri!.ToString().Should().Be("https://login.test/oauth2/v2.0/token");
        var form = ParseForm(handler.CapturedBody);
        form["grant_type"].Should().Be("authorization_code");
        form["code"].Should().Be("the-code");
        form["redirect_uri"].Should().Be("https://app/cb");
        form["client_id"].Should().Be("cid");
        form["client_secret"].Should().Be("test-secret");
        form["scope"].Should().Be("offline_access Calendars.ReadWrite User.Read");

        result.AccessToken.Should().Be("AT");
        result.RefreshToken.Should().Be("RT");
        result.ExpiresUtc.Should().BeCloseTo(before.AddSeconds(3600), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Refresh_posts_refresh_token_grant()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"access_token\":\"AT2\",\"refresh_token\":\"RT2\",\"expires_in\":7200}"),
        });
        var service = BuildService(handler);

        var result = await service.RefreshAsync("old-refresh");

        var form = ParseForm(handler.CapturedBody);
        form["grant_type"].Should().Be("refresh_token");
        form["refresh_token"].Should().Be("old-refresh");
        form["client_id"].Should().Be("cid");
        form["client_secret"].Should().Be("test-secret");

        result.AccessToken.Should().Be("AT2");
        result.RefreshToken.Should().Be("RT2");
    }

    [Fact]
    public async Task Refresh_without_refresh_token_keeps_the_input_token()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"access_token\":\"AT3\",\"expires_in\":3600}"),
        });
        var service = BuildService(handler);

        var result = await service.RefreshAsync("kept-refresh");

        result.AccessToken.Should().Be("AT3");
        result.RefreshToken.Should().Be("kept-refresh");
    }

    [Fact]
    public async Task NonSuccess_throws_with_status_and_oauth_error_but_not_raw_body()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"error\":\"invalid_grant\",\"error_description\":\"AADSTS70008\"}"),
        });
        var service = BuildService(handler);

        var act = async () => await service.ExchangeCodeAsync("bad-code");

        var ex = await act.Should().ThrowAsync<AuthenticationFailedException>();
        // Status code is surfaced for diagnostics.
        ex.Which.Message.Should().Contain("400");
        // The non-secret OAuth error fields may be surfaced.
        ex.Which.Message.Should().Contain("invalid_grant");
        ex.Which.Message.Should().Contain("AADSTS70008");
    }

    [Fact]
    public async Task NonSuccess_message_never_contains_token_material()
    {
        // A hostile / misconfigured token endpoint that echoes token-like material in a
        // non-success body MUST NOT have that material leak into the exception message.
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"error\":\"invalid_grant\",\"access_token\":\"SECRET_AT\"," +
                "\"refresh_token\":\"SECRET_RT\",\"id_token\":\"SECRET_IT\"}"),
        });
        var service = BuildService(handler);

        var act = async () => await service.ExchangeCodeAsync("bad-code");

        var ex = await act.Should().ThrowAsync<AuthenticationFailedException>();
        var message = ex.Which.Message;
        message.Should().Contain("400");
        message.Should().NotContain("SECRET_AT");
        message.Should().NotContain("SECRET_RT");
        message.Should().NotContain("SECRET_IT");
        message.Should().NotContain("access_token");
        message.Should().NotContain("refresh_token");
        message.Should().NotContain("id_token");
    }

    [Fact]
    public async Task NonSuccess_with_nonjson_body_throws_status_only()
    {
        var handler = new CapturingHandler(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("upstream proxy error: token=LEAK"),
        });
        var service = BuildService(handler);

        var act = async () => await service.ExchangeCodeAsync("bad-code");

        var ex = await act.Should().ThrowAsync<AuthenticationFailedException>();
        ex.Which.Message.Should().Contain("500");
        ex.Which.Message.Should().NotContain("LEAK");
    }
}
