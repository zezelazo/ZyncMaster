using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace SyncMaster.Server.Tests.Panel;

public class PanelEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public PanelEndpointsTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Root_returns_html_containing_SyncMaster()
    {
        var client = _factory.WithWebHostBuilder(_ => { }).CreateClient();

        var resp = await client.GetAsync("/");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("SyncMaster");
    }

    [Fact]
    public async Task Panel_status_returns_connected_and_device_count()
    {
        var client = _factory.WithWebHostBuilder(_ => { }).CreateClient();

        var resp = await client.GetAsync("/api/panel/status");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("connected").ValueKind.Should().BeOneOf(JsonValueKind.True, JsonValueKind.False);
        doc.RootElement.GetProperty("deviceCount").GetInt32().Should().BeGreaterThanOrEqualTo(0);
    }
}
