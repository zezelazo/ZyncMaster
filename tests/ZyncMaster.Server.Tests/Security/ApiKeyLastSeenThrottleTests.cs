using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZyncMaster.Server.Data;
using Xunit;

namespace ZyncMaster.Server.Tests.Security;

// FIX F — the api-key handler must NOT write LastSeenUtc on every request (pure write amplification,
// worse with the new heartbeat). It only persists when the stored value has drifted past the throttle
// window. These exercise the real auth pipeline end-to-end through a device-scoped GET /api/devices.
public sealed class ApiKeyLastSeenThrottleTests
{
    private static (string apiKey, string deviceId) Seed(ServerTestFactory factory, DateTimeOffset lastSeen)
    {
        var deviceId = Guid.NewGuid().ToString("N");
        var generated = ApiKeyGenerator.GenerateKey();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        db.Devices.Add(new DeviceRow
        {
            Id = deviceId,
            UserId = DefaultCurrentUserAccessor.DefaultUserId,
            Name = "Laptop",
            ApiKeyHash = ApiKeyHasher.Hash(generated.Secret),
            KeyId = generated.KeyId,
            CreatedUtc = DateTimeOffset.UtcNow.AddDays(-1),
            LastSeenUtc = lastSeen,
        });
        db.SaveChanges();
        return (generated.ApiKey, deviceId);
    }

    private static async Task<DateTimeOffset?> ReadLastSeen(ServerTestFactory factory, string deviceId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        var row = await db.Devices.AsNoTracking().FirstAsync(d => d.Id == deviceId);
        return row.LastSeenUtc;
    }

    private static HttpRequestMessage DevicesGet(string apiKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/devices");
        req.Headers.Add("X-Api-Key", apiKey);
        return req;
    }

    [Fact]
    public async Task Recent_last_seen_is_not_rewritten_on_each_request()
    {
        using var factory = new ServerTestFactory();
        // LastSeenUtc just a minute ago — well within the 5-minute throttle window.
        var recent = DateTimeOffset.UtcNow.AddMinutes(-1);
        var (apiKey, deviceId) = Seed(factory, recent);
        var client = factory.CreateClient();

        // Several authenticated requests in a row.
        for (var i = 0; i < 4; i++)
            (await client.SendAsync(DevicesGet(apiKey))).StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await ReadLastSeen(factory, deviceId);
        after.Should().BeCloseTo(recent, TimeSpan.FromSeconds(1),
            "a recent LastSeenUtc must not be rewritten on every request (no write amplification)");
    }

    [Fact]
    public async Task Stale_last_seen_is_refreshed_once_past_the_throttle_window()
    {
        using var factory = new ServerTestFactory();
        // LastSeenUtc an hour ago — past the throttle window, so the next request should refresh it.
        var stale = DateTimeOffset.UtcNow.AddHours(-1);
        var (apiKey, deviceId) = Seed(factory, stale);
        var client = factory.CreateClient();

        (await client.SendAsync(DevicesGet(apiKey))).StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await ReadLastSeen(factory, deviceId);
        after.Should().NotBeNull();
        after!.Value.Should().BeAfter(stale.AddMinutes(30),
            "a stale LastSeenUtc must be refreshed to (about) now once past the throttle window");
    }
}
