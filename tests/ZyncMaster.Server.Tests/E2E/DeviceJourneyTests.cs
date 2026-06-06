using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ZyncMaster.Core;
using Xunit;

namespace ZyncMaster.Server.Tests.E2E;

// FULL device-pairing + sync journey across the three actors that really participate:
//   * the unpaired DEVICE (anonymous: no cookie, no api key) calls /api/pair/start and later
//     /api/pair/complete;
//   * the HUMAN (signed-in cookie) visits /pair and approves via /api/devices/approve, which
//     binds the new device to THEM;
//   * the paired DEVICE (the issued api key) then pushes events that mirror into the human's
//     connected destination account, and lists itself via /api/devices.
public class DeviceJourneyTests
{
    [Fact]
    public async Task Device_pairs_with_the_signed_in_user_then_pushes_into_their_account()
    {
        using var h = new E2EHarness();

        // The human signs in first so a panel session + connected account exist.
        var panel = await h.SignInAsync("oid-owner", "owner@test", "Owner");

        // 1. Anonymous device starts pairing: gets a pairingId + a short human-readable code.
        var device = h.Factory.CreateClient();
        var start = await device.PostAsJsonAsync("/api/pair/start", new { name = "Field Phone" });
        start.StatusCode.Should().Be(HttpStatusCode.OK);
        using var startDoc = JsonDocument.Parse(await start.Content.ReadAsStringAsync());
        var pairingId = startDoc.RootElement.GetProperty("pairingId").GetString();
        var code = startDoc.RootElement.GetProperty("code").GetString()!;
        // FIX 1 — start returns a PKCE verifier the initiator must echo on complete.
        var verifier = startDoc.RootElement.GetProperty("verifier").GetString();

        // 2. The signed-in human visits /pair?code=... and sees the approval page for the device.
        var pairPage = await panel.GetAsync($"/pair?code={code}");
        pairPage.StatusCode.Should().Be(HttpStatusCode.OK);
        (await pairPage.Content.ReadAsStringAsync()).Should().Contain("Field Phone");

        // 3. The human approves. Approval binds the device to the human (cookie-gated).
        var approve = await panel.PostAsJsonAsync("/api/devices/approve", new { code });
        approve.StatusCode.Should().Be(HttpStatusCode.OK);
        using var approveDoc = JsonDocument.Parse(await approve.Content.ReadAsStringAsync());
        approveDoc.RootElement.GetProperty("approved").GetBoolean().Should().BeTrue();

        // 4. The device completes (anonymous, with its verifier) and receives its one-time api key.
        var complete = await device.PostAsJsonAsync("/api/pair/complete", new { pairingId, verifier });
        complete.StatusCode.Should().Be(HttpStatusCode.OK);
        using var completeDoc = JsonDocument.Parse(await complete.Content.ReadAsStringAsync());
        completeDoc.RootElement.GetProperty("approved").GetBoolean().Should().BeTrue();
        var apiKey = completeDoc.RootElement.GetProperty("apiKey").GetString();
        apiKey.Should().NotBeNullOrWhiteSpace();

        // The api key now authenticates the device as the human's device.
        var paired = h.DeviceClient(apiKey!);

        // 5. The human creates a pair whose source is OutlookCom (device-pushed) -> their account.
        var pairId = await E2EHarness.CreatePairAsync(
            panel, E2EHarness.DevicePairBody("Outlook -> cloud", "owner@test"));

        // 6. The device pushes its Outlook window into the pair; it mirrors into the human's
        //    destination account, resolved with the human's account ref.
        var push = await paired.PostAsJsonAsync($"/api/pairs/{pairId}/push", new
        {
            events = new[] { E2EHarness.Event("x"), E2EHarness.Event("y") },
        });
        push.StatusCode.Should().Be(HttpStatusCode.OK);
        using var pushDoc = JsonDocument.Parse(await push.Content.ReadAsStringAsync());
        pushDoc.RootElement.GetProperty("created").GetInt32().Should().Be(2);

        h.WriterAccountRefs.Should().ContainSingle().Which.Should().Be("owner@test");
        h.MirroredBatches.Should().ContainSingle().Which.Select(e => e.Id)
            .Should().BeEquivalentTo(new[] { "x", "y" });

        // 7. /api/devices (api key -> device owner) lists the paired device.
        var devices = await paired.GetFromJsonAsync<JsonElement>("/api/devices");
        devices.EnumerateArray().Select(d => d.GetProperty("name").GetString())
            .Should().Contain("Field Phone");
    }

    [Fact]
    public async Task Push_before_pairing_is_unauthorized()
    {
        using var h = new E2EHarness();
        await h.SignInAsync("oid-owner", "owner@test", "Owner");

        // An anonymous device (no issued api key) cannot push: the push surface is api-key gated.
        var anon = h.Factory.CreateClient();
        var push = await anon.PostAsJsonAsync("/api/pairs/anything/push",
            new { events = Array.Empty<AppointmentRecord>() });

        push.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        h.WriterAccountRefs.Should().BeEmpty();
    }

    [Fact]
    public async Task Approval_requires_a_signed_in_human_not_a_device_key()
    {
        using var h = new E2EHarness();
        await h.SignInAsync("oid-owner", "owner@test", "Owner");

        // A device starts pairing.
        var device = h.Factory.CreateClient();
        var start = await device.PostAsJsonAsync("/api/pair/start", new { name = "Phone" });
        using var startDoc = JsonDocument.Parse(await start.Content.ReadAsStringAsync());
        var code = startDoc.RootElement.GetProperty("code").GetString();

        // The device cannot approve itself: /api/devices/approve is cookie-gated, so an
        // anonymous (or api-key) request is 401 and the pending row stays unapproved.
        var selfApprove = await device.PostAsJsonAsync("/api/devices/approve", new { code });
        selfApprove.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var devices = h.Factory.Services.GetRequiredService<IDeviceStore>();
        var pending = await devices.GetPendingByCodeAsync(code!, DateTimeOffset.MinValue);
        pending!.Approved.Should().BeFalse();
    }
}
