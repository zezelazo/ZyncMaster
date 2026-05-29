using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using ZyncMaster.Server.Tests;
using Xunit;

namespace ZyncMaster.Server.Tests.Panel;

public class PanelEndpointsTests : IClassFixture<ServerTestFactory>
{
    private readonly ServerTestFactory _factory;

    public PanelEndpointsTests(ServerTestFactory factory) => _factory = factory;

    [Fact]
    public async Task Root_returns_html_containing_ZyncMaster()
    {
        var client = _factory.WithWebHostBuilder(_ => { }).CreateClient();

        var resp = await client.GetAsync("/");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("Zync Master");
    }

    [Fact]
    public async Task Panel_status_requires_cookie()
    {
        var client = _factory.WithWebHostBuilder(_ => { }).CreateClient();

        var resp = await client.GetAsync("/api/panel/status");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Panel_status_returns_connected_and_device_count()
    {
        var fake = new CookieAuthHelper.FakeIdentityTokenService();
        var factory = new ServerTestFactory().WithFakeIdentity(fake);
        var client = await CookieAuthHelper.SignInAsync(factory);

        var resp = await client.GetAsync("/api/panel/status");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("connected").ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
        doc.RootElement.GetProperty("deviceCount").GetInt32().Should().BeGreaterThanOrEqualTo(0);
    }
}
