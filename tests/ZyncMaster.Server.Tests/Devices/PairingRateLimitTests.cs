using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ZyncMaster.Server.Tests.Devices;

// FIX A — the pairing endpoints (/api/pair/start, /api/pair/complete, /api/devices/approve) are now
// behind a per-IP fixed-window rate limiter. This is the brute-force defense for the pairing code:
// without it a client could grind codes against approve unthrottled. These tests drive the real
// limiter under the test host with a deliberately tiny PermitLimit so the 429 is observable.
public class PairingRateLimitTests
{
    // Build the real host with a low per-IP pairing limit so a handful of requests trips the 429.
    // PairingRateLimitWindowMinutes stays at its default so the window does not reset mid-test.
    private static WebApplicationFactory<Program> NewFactory(int permitLimit) =>
        new ServerTestFactory().WithWebHostBuilder(b =>
            b.UseSetting("Server:PairingMaxPerIp", permitLimit.ToString()));

    [Fact]
    public async Task Pair_start_is_rate_limited_per_ip_after_the_permit_limit()
    {
        using var factory = NewFactory(permitLimit: 3);
        var client = factory.CreateClient();

        // The first `permitLimit` requests are admitted; the next is rejected with 429. Send a few
        // past the limit and assert at least one 429 appears (and that the early ones succeeded).
        var statuses = new System.Collections.Generic.List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            var resp = await client.PostAsJsonAsync("/api/pair/start", new { name = "Device " + i });
            statuses.Add(resp.StatusCode);
        }

        statuses.Take(3).Should().AllBeEquivalentTo(HttpStatusCode.OK,
            "the first requests up to the permit limit are admitted");
        statuses.Should().Contain(HttpStatusCode.TooManyRequests,
            "requests beyond the per-IP permit limit are rejected with 429");
    }

    [Fact]
    public async Task Pair_complete_is_rate_limited_per_ip()
    {
        using var factory = NewFactory(permitLimit: 2);
        var client = factory.CreateClient();

        var statuses = new System.Collections.Generic.List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            var resp = await client.PostAsJsonAsync("/api/pair/complete", new { pairingId = "nope-" + i });
            statuses.Add(resp.StatusCode);
        }

        statuses.Should().Contain(HttpStatusCode.TooManyRequests);
    }
}
