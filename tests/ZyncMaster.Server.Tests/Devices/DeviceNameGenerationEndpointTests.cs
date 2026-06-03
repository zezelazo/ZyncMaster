using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZyncMaster.Server.Data;
using Xunit;

namespace ZyncMaster.Server.Tests.Devices;

// End-to-end coverage of default device-name generation through the real Program: registering a
// device WITHOUT a name yields a generated, unique, non-empty geek name; two devices of the same
// user get distinct names; and GET /api/devices/me backfills a nameless device's name.
public class DeviceNameGenerationEndpointTests
{
    private static (string token, string userId) IssueToken(
        WebApplicationFactory<Program> factory, string subject, string email)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserStore>();
        var tokens = scope.ServiceProvider.GetRequiredService<IIdentityTokenService>();
        var user = users.UpsertByLoginAsync("local", subject, email, true, "Gen User", CancellationToken.None)
            .GetAwaiter().GetResult();
        return (tokens.IssueAccessToken(user).Token, user.Id);
    }

    private static async Task<(string apiKey, string deviceId)> RegisterDeviceNoName(
        HttpClient client, string token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/devices/register")
        {
            // Empty name on purpose — the server must generate one.
            Content = JsonContent.Create(new { name = "" }),
        };
        req.Headers.Add("Authorization", $"Bearer {token}");
        var resp = await client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("apiKey").GetString()!,
                doc.RootElement.GetProperty("deviceId").GetString()!);
    }

    [Fact]
    public async Task Register_without_name_persists_generated_unique_name()
    {
        using var factory = new ServerTestFactory();
        var (token, _) = IssueToken(factory, "gen-subj", "zezelazo@msn.com");
        var client = factory.CreateClient();

        var (_, deviceId) = await RegisterDeviceNoName(client, token);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        var row = await db.Devices.FirstAsync(d => d.Id == deviceId);
        row.Name.Should().NotBeNullOrWhiteSpace();
        row.Name.Should().NotBe("Device");
        row.Name.Should().EndWith("-zezelazo");
        row.Name.Length.Should().BeLessThanOrEqualTo(100);
    }

    [Fact]
    public async Task Two_nameless_devices_for_same_user_get_distinct_names()
    {
        using var factory = new ServerTestFactory();
        var (token, _) = IssueToken(factory, "gen-two", "twins@example.com");
        var client = factory.CreateClient();

        var (_, id1) = await RegisterDeviceNoName(client, token);
        var (_, id2) = await RegisterDeviceNoName(client, token);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        var n1 = (await db.Devices.FirstAsync(d => d.Id == id1)).Name;
        var n2 = (await db.Devices.FirstAsync(d => d.Id == id2)).Name;

        n1.Should().NotBeNullOrWhiteSpace();
        n2.Should().NotBeNullOrWhiteSpace();
        string.Equals(n1, n2, StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    [Fact]
    public async Task GetDevicesMe_backfills_name_for_nameless_device()
    {
        using var factory = new ServerTestFactory();
        var (token, userId) = IssueToken(factory, "gen-heal", "healme@example.com");
        var client = factory.CreateClient();
        var (apiKey, deviceId) = await RegisterDeviceNoName(client, token);

        // Force the device back to a blank name to simulate a legacy row.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
            var row = await db.Devices.FirstAsync(d => d.Id == deviceId);
            row.Name = "";
            await db.SaveChangesAsync();
        }

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/devices/me");
        req.Headers.Add("X-Api-Key", apiKey);
        var resp = await client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var name = doc.RootElement.GetProperty("name").GetString();
        name.Should().NotBeNullOrWhiteSpace();
        name.Should().EndWith("-healme");

        // Persisted.
        using var verify = factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        (await vdb.Devices.FirstAsync(d => d.Id == deviceId)).Name.Should().Be(name);
    }
}
