using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace ZyncMaster.Server.Tests.Security;

// FIX 3 + FIX 4 — the unauthenticated token surfaces (/identity/handle/redeem, /identity/refresh,
// /identity/magic-link/callback) and the destructive cron trigger (/api/sync/run-due) are now behind
// per-IP fixed-window rate limiters. Each accepts a bearer-style secret directly in the request, so
// without a limiter they are grindable. These tests drive the real limiter under the test host with a
// deliberately tiny PermitLimit so the 429 is observable.
public sealed class TokenSurfaceRateLimitTests
{
    private static WebApplicationFactory<Program> NewIdentityFactory(int permitLimit) =>
        new ServerTestFactory().WithWebHostBuilder(b =>
            b.UseSetting("Server:IdentityTokenMaxPerIp", permitLimit.ToString()));

    private static WebApplicationFactory<Program> NewCronFactory(int permitLimit, string secret) =>
        new ServerTestFactory().WithWebHostBuilder(b =>
        {
            b.UseSetting("Server:CronTriggerMaxPerIp", permitLimit.ToString());
            b.UseSetting("Server:CronTriggerSecret", secret);
        });

    [Fact]
    public async Task Handle_redeem_is_rate_limited_per_ip()
    {
        using var factory = NewIdentityFactory(permitLimit: 3);
        var client = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++)
        {
            var resp = await client.PostAsJsonAsync("/identity/handle/redeem", new { handle = "nope-" + i });
            statuses.Add(resp.StatusCode);
        }

        statuses.Should().Contain(HttpStatusCode.TooManyRequests,
            "redeem attempts beyond the per-IP permit limit are rejected with 429");
    }

    [Fact]
    public async Task Identity_refresh_is_rate_limited_per_ip()
    {
        using var factory = NewIdentityFactory(permitLimit: 3);
        var client = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++)
        {
            var resp = await client.PostAsJsonAsync("/identity/refresh", new { refreshToken = "nope-" + i });
            statuses.Add(resp.StatusCode);
        }

        statuses.Should().Contain(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Magic_link_callback_is_rate_limited_per_ip()
    {
        using var factory = NewIdentityFactory(permitLimit: 3);
        var client = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++)
        {
            var resp = await client.GetAsync("/identity/magic-link/callback?token=nope-" + i);
            statuses.Add(resp.StatusCode);
        }

        statuses.Should().Contain(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Cron_run_due_is_rate_limited_per_ip_even_with_a_valid_secret()
    {
        const string secret = "0123456789abcdef0123456789abcdef"; // 32 chars
        using var factory = NewCronFactory(permitLimit: 3, secret);
        var client = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/sync/run-due");
            req.Headers.Add("X-Cron-Secret", secret);
            var resp = await client.SendAsync(req);
            statuses.Add(resp.StatusCode);
        }

        statuses.Should().Contain(HttpStatusCode.TooManyRequests,
            "the cron trigger limiter caps hits per IP as defense-in-depth on top of the secret");
    }
}
