using System;
using System.Collections.Generic;
using System.Linq;
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
using Microsoft.Extensions.DependencyInjection.Extensions;
using ZyncMaster.Server.Data;
using ZyncMaster.Server.Infrastructure.Email;
using Xunit;

namespace ZyncMaster.Server.Tests.Identity;

public class MagicLinkEndpointsTests
{
    // Captures the most recent email (and extracts the magic-link token from its body) so tests
    // can assert what was actually sent without any network. Thread-safe enough for the test host.
    private sealed class CapturingEmailSender : IEmailSender
    {
        public int SendCount;
        public string? LastTo;
        public string? LastBody;
        public string? LastToken;

        public Task SendAsync(string toEmail, string subject, string htmlBody, CancellationToken ct = default)
        {
            Interlocked.Increment(ref SendCount);
            LastTo = toEmail;
            LastBody = htmlBody;
            LastToken = ExtractToken(htmlBody);
            return Task.CompletedTask;
        }

        private static string? ExtractToken(string body)
        {
            const string marker = "token=";
            var i = body.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return null;
            var start = i + marker.Length;
            var end = body.IndexOfAny(new[] { '"', '&', '<', ' ' }, start);
            if (end < 0) end = body.Length;
            return Uri.UnescapeDataString(body[start..end]);
        }
    }

    // Controllable clock so TTL/expiry is deterministic.
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private static WebApplicationFactory<Program> CreateFactory(
        CapturingEmailSender sender, FakeClock? clock = null) =>
        new ServerTestFactory().WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IEmailSender>();
                s.AddSingleton<IEmailSender>(sender);
                if (clock is not null)
                {
                    s.RemoveAll<TimeProvider>();
                    s.AddSingleton<TimeProvider>(clock);
                }
            }));

    private static HttpClient NoRedirectClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static string ExtractQueryValue(Uri location, string key)
    {
        var query = location.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            if (Uri.UnescapeDataString(pair[..idx]) == key)
                return Uri.UnescapeDataString(pair[(idx + 1)..]);
        }
        throw new InvalidOperationException($"{key} not found in {location}");
    }

    private static object Body(string email, int port = 51820, string nonce = "app-nonce") =>
        new { email, port, nonce };

    [Fact]
    public async Task Post_new_and_unknown_email_both_return_202_with_same_body_and_capture_token()
    {
        var sender = new CapturingEmailSender();
        using var factory = CreateFactory(sender);
        var client = NoRedirectClient(factory);

        // "Unknown" email — no pre-existing user.
        var resp1 = await client.PostAsJsonAsync("/identity/magic-link", Body("stranger@example.com"));
        resp1.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body1 = await resp1.Content.ReadAsStringAsync();
        sender.LastToken.Should().NotBeNullOrEmpty();

        // Seed a user for the second email so it is "known", then request again.
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<IUserStore>();
            await users.UpsertByLoginAsync(
                "local", "known@example.com", "known@example.com", true, "known@example.com");
        }

        var resp2 = await client.PostAsJsonAsync("/identity/magic-link", Body("known@example.com"));
        resp2.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body2 = await resp2.Content.ReadAsStringAsync();

        // Anti-enumeration: identical response body whether or not the user exists.
        body2.Should().Be(body1);
        sender.SendCount.Should().Be(2);
    }

    [Fact]
    public async Task Callback_with_valid_token_upserts_verified_user_and_redirects_to_loopback()
    {
        var sender = new CapturingEmailSender();
        using var factory = CreateFactory(sender);
        var client = NoRedirectClient(factory);

        var email = "verify@example.com";
        await client.PostAsJsonAsync("/identity/magic-link", Body(email, port: 51830, nonce: "n-verify"));
        var token = sender.LastToken!;

        var resp = await client.GetAsync($"/identity/magic-link/callback?token={Uri.EscapeDataString(token)}");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = resp.Headers.Location!;
        location.Scheme.Should().Be("http");
        location.Host.Should().Be("127.0.0.1");
        location.Port.Should().Be(51830);
        location.AbsolutePath.Should().Be("/identity/callback");
        ExtractQueryValue(location, "nonce").Should().Be("n-verify");
        ExtractQueryValue(location, "handle").Should().NotBeNullOrEmpty();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        // The local login was created AND marked verified (magic-link proves possession).
        var login = db.IdentityLogins.FirstOrDefault(l => l.Provider == "local" && l.Email == email);
        login.Should().NotBeNull();
        login!.EmailVerified.Should().BeTrue();
    }

    [Fact]
    public async Task Callback_token_redeemed_via_handle_yields_validatable_access_token()
    {
        var sender = new CapturingEmailSender();
        using var factory = CreateFactory(sender);
        var client = NoRedirectClient(factory);

        await client.PostAsJsonAsync("/identity/magic-link", Body("handle@example.com", port: 51831, nonce: "h"));
        var token = sender.LastToken!;
        var callback = await client.GetAsync($"/identity/magic-link/callback?token={Uri.EscapeDataString(token)}");
        var handle = ExtractQueryValue(callback.Headers.Location!, "handle");

        var redeem = await client.PostAsJsonAsync("/identity/handle/redeem", new { handle });
        redeem.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await redeem.Content.ReadAsStringAsync());
        var accessToken = doc.RootElement.GetProperty("accessToken").GetString();
        accessToken.Should().NotBeNullOrEmpty();

        var tokens = factory.Services.GetRequiredService<IIdentityTokenService>();
        tokens.ValidateAccessToken(accessToken!).Should().NotBeNull();
    }

    [Fact]
    public async Task Callback_with_already_consumed_token_fails_single_use()
    {
        var sender = new CapturingEmailSender();
        using var factory = CreateFactory(sender);
        var client = NoRedirectClient(factory);

        await client.PostAsJsonAsync("/identity/magic-link", Body("single@example.com"));
        var token = sender.LastToken!;

        var first = await client.GetAsync($"/identity/magic-link/callback?token={Uri.EscapeDataString(token)}");
        first.StatusCode.Should().Be(HttpStatusCode.Redirect);

        var second = await client.GetAsync($"/identity/magic-link/callback?token={Uri.EscapeDataString(token)}");
        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Callback_with_expired_token_fails()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var sender = new CapturingEmailSender();
        using var factory = CreateFactory(sender, clock);
        var client = NoRedirectClient(factory);

        await client.PostAsJsonAsync("/identity/magic-link", Body("expired@example.com"));
        var token = sender.LastToken!;

        // Advance past the default 15-minute TTL.
        clock.Advance(TimeSpan.FromMinutes(16));

        var resp = await client.GetAsync($"/identity/magic-link/callback?token={Uri.EscapeDataString(token)}");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Callback_with_unknown_token_fails()
    {
        var sender = new CapturingEmailSender();
        using var factory = CreateFactory(sender);
        var client = NoRedirectClient(factory);

        var resp = await client.GetAsync("/identity/magic-link/callback?token=not-a-real-token");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Per_email_rate_limit_goes_silent_after_cap_but_keeps_returning_202()
    {
        var sender = new CapturingEmailSender();
        using var factory = CreateFactory(sender);
        var client = NoRedirectClient(factory);

        var email = "flood@example.com";
        // Default MagicLinkMaxPerEmail is 3.
        for (var i = 0; i < 3; i++)
        {
            var ok = await client.PostAsJsonAsync("/identity/magic-link", Body(email, nonce: $"n{i}"));
            ok.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }
        sender.SendCount.Should().Be(3);

        // 4th request inside the window: still 202 (no enumeration leak) but NO email sent.
        var fourth = await client.PostAsJsonAsync("/identity/magic-link", Body(email, nonce: "n4"));
        fourth.StatusCode.Should().Be(HttpStatusCode.Accepted);
        sender.SendCount.Should().Be(3);
    }

    [Theory]
    [InlineData(80, "app-nonce")]      // port below 1024
    [InlineData(70000, "app-nonce")]   // port above 65535
    [InlineData(51820, "")]            // empty nonce
    public async Task Post_with_bad_port_or_nonce_returns_400(int port, string nonce)
    {
        var sender = new CapturingEmailSender();
        using var factory = CreateFactory(sender);
        var client = NoRedirectClient(factory);

        var resp = await client.PostAsJsonAsync(
            "/identity/magic-link", new { email = "x@example.com", port, nonce });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        sender.SendCount.Should().Be(0);
    }

    [Fact]
    public async Task Post_with_missing_email_returns_400()
    {
        var sender = new CapturingEmailSender();
        using var factory = CreateFactory(sender);
        var client = NoRedirectClient(factory);

        var resp = await client.PostAsJsonAsync(
            "/identity/magic-link", new { email = "", port = 51820, nonce = "n" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
