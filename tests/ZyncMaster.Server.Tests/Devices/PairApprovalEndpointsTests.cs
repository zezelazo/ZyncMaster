using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ZyncMaster.Server.Data;

namespace ZyncMaster.Server.Tests.Devices;

// The pairing flow is cross-actor:
//   * The unpaired DEVICE calls /api/pair/start and /api/pair/complete anonymously (no
//     cookie, no api key) — the ambient user is "default".
//   * The HUMAN approves in a signed-in browser (cookie) via /pair → /api/devices/approve.
// So /pair and /api/devices/approve are gated on a signed-in user, and the created device
// binds to that user. Pending-pairing rows are global so the approver (a different actor
// from the device that created the row) can find them by code.
public class PairApprovalEndpointsTests
{
    private static WebApplicationFactory<Program> NewFactory() =>
        new ServerTestFactory().WithFakeIdentity(new CookieAuthHelper.FakeIdentityTokenService());

    private static HttpClient NonRedirectingClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<string> SeedPendingAsync(WebApplicationFactory<Program> factory, string code, string name)
    {
        var devices = factory.Services.GetRequiredService<IDeviceStore>();
        await devices.SavePendingAsync(new PendingPairing
        {
            PairingId = Guid.NewGuid().ToString("N"),
            DeviceName = name,
            Code = code,
            CreatedUtc = DateTimeOffset.UtcNow,
        });
        return code;
    }

    // The cookie-bearing browser client also has to not auto-follow the (none expected)
    // redirects, but it must keep its session cookie. SignInAsync already returns a
    // cookie-holding, non-redirecting client.
    private static Task<HttpClient> SignedInBrowserAsync(WebApplicationFactory<Program> factory) =>
        CookieAuthHelper.SignInAsync(factory);

    [Fact]
    public async Task Unauthenticated_pair_redirects_to_connect_with_returnTo()
    {
        var factory = NewFactory();
        var client = NonRedirectingClient(factory);

        var resp = await client.GetAsync("/pair?code=ABC123");

        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = resp.Headers.Location!.ToString();
        location.Should().StartWith("/connect?returnTo=");
        Uri.UnescapeDataString(location).Should().Contain("/pair?code=ABC123");
    }

    [Fact]
    public async Task Authenticated_pair_shows_device_name_code_and_approve()
    {
        var factory = NewFactory();
        await SeedPendingAsync(factory, "ABC123", "Zeze Laptop");
        var client = await SignedInBrowserAsync(factory);

        var resp = await client.GetAsync("/pair?code=ABC123");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("Zeze Laptop");
        html.Should().Contain("ABC123");
        html.Should().Contain("/api/devices/approve");
    }

    [Fact]
    public async Task Authenticated_unknown_code_shows_invalid_message()
    {
        var factory = NewFactory();
        var client = await SignedInBrowserAsync(factory);

        var resp = await client.GetAsync("/pair?code=NOPE99");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("not valid");
    }

    [Fact]
    public async Task Authenticated_already_approved_code_shows_approved_message()
    {
        var factory = NewFactory();
        var devices = factory.Services.GetRequiredService<IDeviceStore>();
        await devices.SavePendingAsync(new PendingPairing
        {
            PairingId = Guid.NewGuid().ToString("N"),
            DeviceName = "Tablet",
            Code = "DONE11",
            Approved = true,
            CreatedUtc = DateTimeOffset.UtcNow,
        });
        var client = await SignedInBrowserAsync(factory);

        var resp = await client.GetAsync("/pair?code=DONE11");

        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("already approved");
    }

    [Fact]
    public async Task Authenticated_missing_code_shows_no_code_message()
    {
        var factory = NewFactory();
        var client = await SignedInBrowserAsync(factory);

        var resp = await client.GetAsync("/pair");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("No pairing code");
    }

    [Fact]
    public async Task Approve_without_cookie_returns_401()
    {
        var factory = NewFactory();
        await SeedPendingAsync(factory, "NOCOOK", "Phone");
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/devices/approve", new { code = "NOCOOK" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // The pending row must remain unapproved — the anonymous request was rejected.
        var devices = factory.Services.GetRequiredService<IDeviceStore>();
        var pending = await devices.GetPendingByCodeAsync("NOCOOK", DateTimeOffset.MinValue);
        pending!.Approved.Should().BeFalse();
    }

    [Fact]
    public async Task Cross_actor_flow_binds_device_to_signed_in_user()
    {
        var fake = new CookieAuthHelper.FakeIdentityTokenService
        {
            Subject = "oid-cross",
            Upn = "cross@test",
            DisplayName = "Cross Actor",
        };
        var factory = new ServerTestFactory().WithFakeIdentity(fake);

        // 1. Device starts pairing anonymously (no cookie). Stored under the "default" user.
        var deviceClient = factory.CreateClient();
        var startResp = await deviceClient.PostAsJsonAsync("/api/pair/start", new { name = "Field Phone" });
        startResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var startDoc = JsonDocument.Parse(await startResp.Content.ReadAsStringAsync());
        var pairingId = startDoc.RootElement.GetProperty("pairingId").GetString();
        var code = startDoc.RootElement.GetProperty("code").GetString();
        // FIX 1 — start returns a PKCE verifier the initiator must echo on complete.
        var verifier = startDoc.RootElement.GetProperty("verifier").GetString();

        // 2. Human approves from a signed-in browser. The cookie request finds the global
        //    pending row and creates the device.
        var browser = await SignedInBrowserAsync(factory);
        var approveResp = await browser.PostAsJsonAsync("/api/devices/approve", new { code });
        approveResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var approveDoc = JsonDocument.Parse(await approveResp.Content.ReadAsStringAsync());
        approveDoc.RootElement.GetProperty("approved").GetBoolean().Should().BeTrue();

        // 3. Device completes anonymously (with its verifier) and receives the api key.
        var completeResp = await deviceClient.PostAsJsonAsync("/api/pair/complete", new { pairingId, verifier });
        completeResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var completeDoc = JsonDocument.Parse(await completeResp.Content.ReadAsStringAsync());
        completeDoc.RootElement.GetProperty("approved").GetBoolean().Should().BeTrue();
        completeDoc.RootElement.GetProperty("apiKey").GetString().Should().NotBeNullOrWhiteSpace();

        // 4. The created device row must belong to the signed-in user, NOT "default".
        var dbFactory = factory.Services.GetRequiredService<IDbContextFactory<ZyncMasterDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        var signedInUser = await db.Users.AsNoTracking()
            .SingleAsync(u => u.Provider == "microsoft" && u.Subject == "oid-cross");
        var deviceRow = await db.Devices.AsNoTracking()
            .SingleAsync(d => d.Name == "Field Phone");

        deviceRow.UserId.Should().Be(signedInUser.Id);
        deviceRow.UserId.Should().NotBe(DefaultCurrentUserAccessor.DefaultUserId);
    }
}
