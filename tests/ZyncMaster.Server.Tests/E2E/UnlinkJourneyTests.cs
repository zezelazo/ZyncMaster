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

// FULL unlink journey: connect an account, create a pair that targets it, then DELETE the
// account. The unlink must disable the referencing pair AND forget the account, so any later
// device push/run against that pair can no longer mirror to a live account -> 409.
public class UnlinkJourneyTests
{
    [Fact]
    public async Task Unlinking_the_account_disables_the_pair_and_blocks_a_later_device_push()
    {
        using var h = new E2EHarness();

        // Connect account + create a device-sourced pair into it.
        var panel = await h.SignInAsync("oid-zeze", "zeze@test", "Zeze");
        var pairId = await E2EHarness.CreatePairAsync(
            panel, E2EHarness.DevicePairBody("Outlook -> cloud", "zeze@test"));

        var userId = await h.UserIdAsync("oid-zeze", "zeze@test", "Zeze");
        var device = h.DeviceClient(await h.AddDeviceForUserAsync(userId));

        // A push works while the account is connected.
        var ok = await device.PostAsJsonAsync($"/api/pairs/{pairId}/push", new
        {
            events = new[] { E2EHarness.Event("a") },
        });
        ok.StatusCode.Should().Be(HttpStatusCode.OK);

        // Unlink the account. The cascade disables the referencing pair and reports its id.
        var unlink = await panel.DeleteAsync("/api/accounts/zeze@test");
        unlink.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = JsonDocument.Parse(await unlink.Content.ReadAsStringAsync()))
        {
            doc.RootElement.GetProperty("affectedPairIds")
                .EnumerateArray().Select(e => e.GetString())
                .Should().BeEquivalentTo(new[] { pairId });
        }

        // The account is forgotten...
        (await panel.GetFromJsonAsync<JsonElement>("/api/accounts")).GetArrayLength().Should().Be(0);
        // ...and the pair is disabled (still visible to the owner, but not active).
        (await panel.GetFromJsonAsync<JsonElement>($"/api/pairs/{pairId}"))
            .GetProperty("state").GetString().Should().Be("disabled");
    }

    [Fact]
    public async Task Unlinking_then_a_device_sync_returns_409_no_connected_account()
    {
        using var h = new E2EHarness();

        var panel = await h.SignInAsync("oid-zeze", "zeze@test", "Zeze");
        var userId = await h.UserIdAsync("oid-zeze", "zeze@test", "Zeze");
        var device = h.DeviceClient(await h.AddDeviceForUserAsync(userId));

        // While connected, the device sync mirrors into the account (resolved with the owner ref).
        var before = await device.PostAsJsonAsync("/api/sync/calendar", new SyncRequest
        {
            Events = new() { E2EHarness.Event("a") },
        });
        before.StatusCode.Should().Be(HttpStatusCode.OK);
        h.SyncTargetAccountRefs.Should().ContainSingle().Which.Should().Be("zeze@test");

        // The owner unlinks their account from the panel.
        (await panel.DeleteAsync("/api/accounts/zeze@test")).StatusCode.Should().Be(HttpStatusCode.OK);

        // The device sync now has no account to mirror to -> 409 no_connected_account, and the
        // target factory was NOT invoked a second time.
        var after = await device.PostAsJsonAsync("/api/sync/calendar", new SyncRequest
        {
            Events = new() { E2EHarness.Event("b") },
        });
        after.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var doc = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetString().Should().Be("no_connected_account");
        h.SyncTargetAccountRefs.Should().ContainSingle("the unlink must stop further mirrors");
    }
}
