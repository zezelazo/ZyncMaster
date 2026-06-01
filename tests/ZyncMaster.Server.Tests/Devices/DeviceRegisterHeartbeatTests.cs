using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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

// §A-2 brokered registration + Track B heartbeat, exercised end-to-end through the real Program.
public class DeviceRegisterHeartbeatTests
{
    private static (string token, string userId) IssueToken(
        WebApplicationFactory<Program> factory, string subject, string email)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserStore>();
        var tokens = scope.ServiceProvider.GetRequiredService<IIdentityTokenService>();
        var user = users.UpsertByLoginAsync("local", subject, email, true, "Reg User", CancellationToken.None)
            .GetAwaiter().GetResult();
        return (tokens.IssueAccessToken(user).Token, user.Id);
    }

    private static HttpRequestMessage RegisterReq(string? bearer, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/devices/register")
        {
            Content = JsonContent.Create(body),
        };
        if (bearer is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return req;
    }

    [Fact]
    public async Task Register_with_valid_bearer_creates_device_under_token_user()
    {
        using var factory = new ServerTestFactory();
        var (token, userId) = IssueToken(factory, "reg-subj", "reg@example.com");
        var client = factory.CreateClient();

        var resp = await client.SendAsync(RegisterReq(token, new
        {
            name = "Workstation",
            platform = "windows",
            hasOutlookCom = true,
            appVersion = "1.0.0",
        }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var deviceId = doc.RootElement.GetProperty("deviceId").GetString();
        doc.RootElement.GetProperty("apiKey").GetString().Should().Contain(".");
        doc.RootElement.GetProperty("leaseUntil").GetDateTimeOffset().Should().BeAfter(DateTimeOffset.UtcNow);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        var row = await db.Devices.FirstAsync(d => d.Id == deviceId);
        row.UserId.Should().Be(userId, "the device must bind to the token's user");
        row.HasOutlookCom.Should().BeTrue();
        row.LeaseUntil.Should().NotBeNull();
        row.KeyId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Register_ignores_foreign_userId_in_body()
    {
        // The body carries a userId for some OTHER user; it must be ignored — the device binds to
        // the bearer token's user, not the body. The request type has no userId field, so the
        // extra JSON property is simply not bound; this asserts the resulting ownership.
        using var factory = new ServerTestFactory();
        var (token, userId) = IssueToken(factory, "owner-subj", "owner@example.com");
        var client = factory.CreateClient();

        var resp = await client.SendAsync(RegisterReq(token, new
        {
            name = "Workstation",
            userId = "someone-elses-user-id",
            ownerId = "someone-elses-user-id",
        }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var deviceId = doc.RootElement.GetProperty("deviceId").GetString();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        var row = await db.Devices.FirstAsync(d => d.Id == deviceId);
        row.UserId.Should().Be(userId);
        row.UserId.Should().NotBe("someone-elses-user-id");
    }

    [Fact]
    public async Task Register_without_bearer_returns_401()
    {
        using var factory = new ServerTestFactory();
        var client = factory.CreateClient();
        var resp = await client.SendAsync(RegisterReq(null, new { name = "X" }));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Register_then_use_api_key_authenticates_via_indexed_lookup()
    {
        // The registered key must authenticate against a device-scoped endpoint, proving the
        // keyId.secret format + indexed lookup path works end to end.
        using var factory = new ServerTestFactory();
        var (token, _) = IssueToken(factory, "reg2-subj", "reg2@example.com");
        var client = factory.CreateClient();

        var reg = await client.SendAsync(RegisterReq(token, new { name = "W" }));
        using var doc = JsonDocument.Parse(await reg.Content.ReadAsStringAsync());
        var apiKey = doc.RootElement.GetProperty("apiKey").GetString()!;

        var devicesReq = new HttpRequestMessage(HttpMethod.Get, "/api/devices");
        devicesReq.Headers.Add("X-Api-Key", apiKey);
        var resp = await client.SendAsync(devicesReq);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Heartbeat_with_valid_api_key_renews_lease()
    {
        using var factory = new ServerTestFactory();
        var (token, _) = IssueToken(factory, "hb-subj", "hb@example.com");
        var client = factory.CreateClient();

        var reg = await client.SendAsync(RegisterReq(token, new { name = "W" }));
        using var regDoc = JsonDocument.Parse(await reg.Content.ReadAsStringAsync());
        var apiKey = regDoc.RootElement.GetProperty("apiKey").GetString()!;
        var deviceId = regDoc.RootElement.GetProperty("deviceId").GetString()!;
        var firstLease = regDoc.RootElement.GetProperty("leaseUntil").GetDateTimeOffset();

        var hbReq = new HttpRequestMessage(HttpMethod.Post, "/api/devices/heartbeat");
        hbReq.Headers.Add("X-Api-Key", apiKey);
        hbReq.Content = JsonContent.Create(new { });
        var hb = await client.SendAsync(hbReq);

        hb.StatusCode.Should().Be(HttpStatusCode.OK);
        using var hbDoc = JsonDocument.Parse(await hb.Content.ReadAsStringAsync());
        var renewed = hbDoc.RootElement.GetProperty("leaseUntil").GetDateTimeOffset();
        renewed.Should().BeOnOrAfter(firstLease);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        var row = await db.Devices.FirstAsync(d => d.Id == deviceId);
        row.LeaseUntil.Should().BeCloseTo(renewed, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Heartbeat_without_api_key_returns_401()
    {
        using var factory = new ServerTestFactory();
        var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/devices/heartbeat", new { });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
