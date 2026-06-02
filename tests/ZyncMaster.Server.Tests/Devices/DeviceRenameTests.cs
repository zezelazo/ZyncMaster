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

// Device self-rename + self-read, exercised end-to-end through the real Program. A device
// authenticates with its api key; the deviceId is read from the ApiKey principal, NEVER from the
// body, so a device can only ever rename ITSELF and never another user's device.
public class DeviceRenameTests
{
    private static (string token, string userId) IssueToken(
        WebApplicationFactory<Program> factory, string subject, string email)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserStore>();
        var tokens = scope.ServiceProvider.GetRequiredService<IIdentityTokenService>();
        var user = users.UpsertByLoginAsync("local", subject, email, true, "Rename User", CancellationToken.None)
            .GetAwaiter().GetResult();
        return (tokens.IssueAccessToken(user).Token, user.Id);
    }

    // Registers a device for the token's user and returns (apiKey, deviceId).
    private static async Task<(string apiKey, string deviceId)> RegisterDevice(
        HttpClient client, string token, string name = "Initial Name")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/devices/register")
        {
            Content = JsonContent.Create(new { name }),
        };
        req.Headers.Add("Authorization", $"Bearer {token}");
        var resp = await client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("apiKey").GetString()!,
                doc.RootElement.GetProperty("deviceId").GetString()!);
    }

    private static HttpRequestMessage RenameReq(string? apiKey, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/devices/rename")
        {
            Content = JsonContent.Create(body),
        };
        if (apiKey is not null)
            req.Headers.Add("X-Api-Key", apiKey);
        return req;
    }

    [Fact]
    public async Task Rename_with_valid_api_key_changes_caller_device_name()
    {
        using var factory = new ServerTestFactory();
        var (token, _) = IssueToken(factory, "rn-subj", "rn@example.com");
        var client = factory.CreateClient();
        var (apiKey, deviceId) = await RegisterDevice(client, token);

        var resp = await client.SendAsync(RenameReq(apiKey, new { name = "Office Laptop" }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("deviceId").GetString().Should().Be(deviceId);
        doc.RootElement.GetProperty("name").GetString().Should().Be("Office Laptop");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        var row = await db.Devices.FirstAsync(d => d.Id == deviceId);
        row.Name.Should().Be("Office Laptop");
    }

    [Fact]
    public async Task Rename_trims_whitespace()
    {
        using var factory = new ServerTestFactory();
        var (token, _) = IssueToken(factory, "rn-trim", "trim@example.com");
        var client = factory.CreateClient();
        var (apiKey, _) = await RegisterDevice(client, token);

        var resp = await client.SendAsync(RenameReq(apiKey, new { name = "  Padded  " }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("name").GetString().Should().Be("Padded");
    }

    [Fact]
    public async Task Rename_empty_name_returns_400()
    {
        using var factory = new ServerTestFactory();
        var (token, _) = IssueToken(factory, "rn-empty", "empty@example.com");
        var client = factory.CreateClient();
        var (apiKey, _) = await RegisterDevice(client, token);

        var resp = await client.SendAsync(RenameReq(apiKey, new { name = "   " }));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rename_too_long_name_returns_400()
    {
        using var factory = new ServerTestFactory();
        var (token, _) = IssueToken(factory, "rn-long", "long@example.com");
        var client = factory.CreateClient();
        var (apiKey, _) = await RegisterDevice(client, token);

        var resp = await client.SendAsync(RenameReq(apiKey, new { name = new string('x', 101) }));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rename_without_api_key_returns_401()
    {
        using var factory = new ServerTestFactory();
        var client = factory.CreateClient();

        var resp = await client.SendAsync(RenameReq(null, new { name = "X" }));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Rename_ignores_deviceId_in_body_and_only_touches_caller_device()
    {
        // Two devices for the SAME user. The caller (device A) sends a body that smuggles device B's
        // id. The server must ignore the body id and rename ONLY device A (the ApiKey principal).
        using var factory = new ServerTestFactory();
        var (token, _) = IssueToken(factory, "rn-self", "self@example.com");
        var client = factory.CreateClient();
        var (apiKeyA, deviceIdA) = await RegisterDevice(client, token, "Device A");
        var (_, deviceIdB) = await RegisterDevice(client, token, "Device B");

        var resp = await client.SendAsync(RenameReq(apiKeyA, new
        {
            name = "Renamed By A",
            deviceId = deviceIdB, // smuggled — must be ignored
            id = deviceIdB,
        }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("deviceId").GetString().Should().Be(deviceIdA);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        (await db.Devices.FirstAsync(d => d.Id == deviceIdA)).Name.Should().Be("Renamed By A");
        (await db.Devices.FirstAsync(d => d.Id == deviceIdB)).Name.Should().Be("Device B", "device B must be untouched");
    }

    [Fact]
    public async Task Rename_is_isolated_across_users()
    {
        // Device of user 1 and device of user 2. User 1's key must NOT be able to affect user 2's
        // device. Since the deviceId comes from the principal and the store is user-scoped, the
        // only device user 1's key can touch is user 1's own device.
        using var factory = new ServerTestFactory();
        var (token1, _) = IssueToken(factory, "u1-subj", "u1@example.com");
        var (token2, _) = IssueToken(factory, "u2-subj", "u2@example.com");
        var client = factory.CreateClient();
        var (apiKey1, deviceId1) = await RegisterDevice(client, token1, "User1 Device");
        var (_, deviceId2) = await RegisterDevice(client, token2, "User2 Device");

        var resp = await client.SendAsync(RenameReq(apiKey1, new { name = "Hijacked" }));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        (await db.Devices.FirstAsync(d => d.Id == deviceId1)).Name.Should().Be("Hijacked");
        (await db.Devices.FirstAsync(d => d.Id == deviceId2)).Name.Should().Be("User2 Device", "another user's device is never affected");
    }

    [Fact]
    public async Task GetDevicesMe_returns_caller_device()
    {
        using var factory = new ServerTestFactory();
        var (token, _) = IssueToken(factory, "me-subj", "me@example.com");
        var client = factory.CreateClient();
        var (apiKey, deviceId) = await RegisterDevice(client, token, "My Workstation");

        var req = new HttpRequestMessage(HttpMethod.Get, "/api/devices/me");
        req.Headers.Add("X-Api-Key", apiKey);
        var resp = await client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("deviceId").GetString().Should().Be(deviceId);
        doc.RootElement.GetProperty("name").GetString().Should().Be("My Workstation");
        doc.RootElement.GetProperty("platform").GetString().Should().Be("windows");
    }

    [Fact]
    public async Task GetDevicesMe_without_api_key_returns_401()
    {
        using var factory = new ServerTestFactory();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/devices/me");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
