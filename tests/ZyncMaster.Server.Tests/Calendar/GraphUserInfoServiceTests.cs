using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace ZyncMaster.Server.Tests.Calendar;

// Unit tests for GraphUserInfoService — the best-effort Graph /me lookup that captures a connected
// account's real mailbox + display name (the calendar token exchange omits openid, so the email
// must be fetched separately). Driven through a stub HttpMessageHandler so no network is touched.
public class GraphUserInfoServiceTests
{
    private sealed class FuncHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
        public List<string> RequestedUrls { get; } = new();
        public List<string?> AuthHeaders { get; } = new();
        public FuncHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestedUrls.Add(request.RequestUri!.ToString());
            AuthHeaders.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(_fn(request));
        }
    }

    private static GraphUserInfoService Service(FuncHandler handler) =>
        new(new HttpClient(handler));

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json) };

    [Fact]
    public async Task GetMe_returns_mail_and_displayName_and_uses_bearer()
    {
        var handler = new FuncHandler(_ => Ok(
            "{\"mail\":\"alice@contoso.com\",\"userPrincipalName\":\"alice_upn@contoso.com\",\"displayName\":\"Alice A\"}"));
        var svc = Service(handler);

        var me = await svc.GetMeAsync("access-123");

        me.Email.Should().Be("alice@contoso.com");
        me.DisplayName.Should().Be("Alice A");
        me.HasEmail.Should().BeTrue();
        handler.RequestedUrls[0].Should().Contain("graph.microsoft.com/v1.0/me");
        handler.AuthHeaders[0].Should().Be("Bearer access-123");
    }

    [Fact]
    public async Task GetMe_falls_back_to_userPrincipalName_when_mail_missing()
    {
        var handler = new FuncHandler(_ => Ok(
            "{\"userPrincipalName\":\"bob@contoso.com\",\"displayName\":\"Bob\"}"));
        var me = await Service(handler).GetMeAsync("t");

        me.Email.Should().Be("bob@contoso.com");
        me.DisplayName.Should().Be("Bob");
    }

    [Fact]
    public async Task GetMe_returns_empty_on_non_2xx()
    {
        var handler = new FuncHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var me = await Service(handler).GetMeAsync("t");

        me.Should().Be(GraphUserInfo.Empty);
        me.HasEmail.Should().BeFalse();
    }

    [Fact]
    public async Task GetMe_returns_empty_on_non_json_body()
    {
        var handler = new FuncHandler(_ => Ok("<html>login</html>"));
        var me = await Service(handler).GetMeAsync("t");
        me.Should().Be(GraphUserInfo.Empty);
    }

    [Fact]
    public async Task GetMe_returns_empty_on_transport_exception()
    {
        var handler = new FuncHandler(_ => throw new HttpRequestException("boom"));
        var me = await Service(handler).GetMeAsync("t");
        me.Should().Be(GraphUserInfo.Empty);
    }

    [Fact]
    public async Task GetMe_returns_empty_for_blank_access_token_without_calling_graph()
    {
        var handler = new FuncHandler(_ => Ok("{\"mail\":\"x@y.com\"}"));
        var me = await Service(handler).GetMeAsync("");

        me.Should().Be(GraphUserInfo.Empty);
        handler.RequestedUrls.Should().BeEmpty();
    }
}
