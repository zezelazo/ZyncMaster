using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace SyncMaster.Server.Tests.Devices;

public class DeviceEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public DeviceEndpointsTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task PairStart_returns_pairingId_and_code()
    {
        var client = _factory.WithWebHostBuilder(_ => { }).CreateClient();

        var resp = await client.PostAsJsonAsync("/api/pair/start", new { name = "Laptop" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("pairingId").GetString().Should().NotBeNullOrWhiteSpace();
        doc.RootElement.GetProperty("code").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task PairStart_empty_name_returns_400()
    {
        var client = _factory.WithWebHostBuilder(_ => { }).CreateClient();

        var resp = await client.PostAsJsonAsync("/api/pair/start", new { name = "" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Full_happy_path_start_approve_complete_and_list()
    {
        var factory = _factory.WithWebHostBuilder(_ => { });
        var client = factory.CreateClient();

        // start
        var startResp = await client.PostAsJsonAsync("/api/pair/start", new { name = "Laptop" });
        startResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var startDoc = JsonDocument.Parse(await startResp.Content.ReadAsStringAsync());
        var pairingId = startDoc.RootElement.GetProperty("pairingId").GetString();
        var code = startDoc.RootElement.GetProperty("code").GetString();

        // approve
        var approveResp = await client.PostAsJsonAsync("/api/devices/approve", new { code });
        approveResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var approveDoc = JsonDocument.Parse(await approveResp.Content.ReadAsStringAsync());
        approveDoc.RootElement.GetProperty("approved").GetBoolean().Should().BeTrue();

        // complete
        var completeResp = await client.PostAsJsonAsync("/api/pair/complete", new { pairingId });
        completeResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var completeDoc = JsonDocument.Parse(await completeResp.Content.ReadAsStringAsync());
        completeDoc.RootElement.GetProperty("approved").GetBoolean().Should().BeTrue();
        var apiKey = completeDoc.RootElement.GetProperty("apiKey").GetString();
        apiKey.Should().NotBeNullOrWhiteSpace();

        // list with api key
        var listClient = factory.CreateClient();
        listClient.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        var listResp = await listClient.GetAsync("/api/devices");
        listResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var listBody = await listResp.Content.ReadAsStringAsync();
        listBody.Should().Contain("Laptop");
        listBody.ToLowerInvariant().Should().NotContain("apikeyhash");
    }

    [Fact]
    public async Task Approve_unknown_code_returns_200_with_approved_false()
    {
        var client = _factory.WithWebHostBuilder(_ => { }).CreateClient();

        var resp = await client.PostAsJsonAsync("/api/devices/approve", new { code = "ZZZZZZ" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("approved").GetBoolean().Should().BeFalse();
    }
}
