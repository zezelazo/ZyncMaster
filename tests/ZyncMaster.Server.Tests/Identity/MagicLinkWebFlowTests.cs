using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ZyncMaster.Server.Infrastructure.Email;
using Xunit;

namespace ZyncMaster.Server.Tests.Identity;

// Web mode of the magic-link flow (the Angular SPA has no loopback listener): `web:true` in the
// request makes the callback redirect to the FIXED same-origin SPA path
// /zync-web/auth/callback?handle=&nonce= instead of http://127.0.0.1:{port}. Handle redeem and
// refresh are the existing endpoints, unchanged.
public class MagicLinkWebFlowTests
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

    private static WebApplicationFactory<Program> CreateFactory(CapturingEmailSender sender) =>
        new ServerTestFactory().WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IEmailSender>();
                s.AddSingleton<IEmailSender>(sender);
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

    [Fact]
    public async Task Web_request_without_port_is_accepted_and_sends_the_link()
    {
        var sender = new CapturingEmailSender();
        using var factory = CreateFactory(sender);
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/identity/magic-link",
            new { email = "web@example.com", web = true, nonce = "n-1" });

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        sender.SendCount.Should().Be(1);
        sender.LastToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Non_web_request_still_requires_a_loopback_port()
    {
        var sender = new CapturingEmailSender();
        using var factory = CreateFactory(sender);
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/identity/magic-link",
            new { email = "app@example.com", nonce = "n-1" }); // no port, no web flag

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        sender.SendCount.Should().Be(0);
    }

    [Fact]
    public async Task Web_callback_redirects_to_the_fixed_spa_path_with_handle_and_nonce()
    {
        var sender = new CapturingEmailSender();
        using var factory = CreateFactory(sender);
        using var client = NoRedirectClient(factory);

        await client.PostAsJsonAsync("/identity/magic-link",
            new { email = "web@example.com", web = true, nonce = "n-42" });

        var cb = await client.GetAsync(
            $"/identity/magic-link/callback?token={Uri.EscapeDataString(sender.LastToken!)}");

        cb.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = cb.Headers.Location!;
        location.OriginalString.Should().StartWith("/zync-web/auth/callback?");
        ExtractQueryValue(new Uri("http://x" + location.OriginalString), "nonce").Should().Be("n-42");
        ExtractQueryValue(new Uri("http://x" + location.OriginalString), "handle").Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Web_handle_redeems_into_a_working_identity_bearer()
    {
        var sender = new CapturingEmailSender();
        using var factory = CreateFactory(sender);
        using var client = NoRedirectClient(factory);

        await client.PostAsJsonAsync("/identity/magic-link",
            new { email = "web@example.com", web = true, nonce = "n-9" });
        var cb = await client.GetAsync(
            $"/identity/magic-link/callback?token={Uri.EscapeDataString(sender.LastToken!)}");
        var handle = ExtractQueryValue(new Uri("http://x" + cb.Headers.Location!.OriginalString), "handle");

        var redeem = await client.PostAsJsonAsync("/identity/handle/redeem", new { handle });
        redeem.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await redeem.Content.ReadAsStringAsync());
        var access = doc.RootElement.GetProperty("accessToken").GetString();
        access.Should().NotBeNullOrEmpty();

        using var me = new HttpRequestMessage(HttpMethod.Get, "/api/identity/me");
        me.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", access);
        var meResp = await client.SendAsync(me);
        meResp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await meResp.Content.ReadAsStringAsync()).Should().Contain("web@example.com");
    }

    [Fact]
    public async Task Web_request_still_requires_a_nonce()
    {
        var sender = new CapturingEmailSender();
        using var factory = CreateFactory(sender);
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/identity/magic-link",
            new { email = "web@example.com", web = true });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
