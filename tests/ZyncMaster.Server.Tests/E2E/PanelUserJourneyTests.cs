using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace ZyncMaster.Server.Tests.E2E;

// FULL panel journey, the way the browser panel drives the server: sign in with a cookie,
// confirm /api/me identifies the user, see the connected account that the OAuth callback
// created, list its calendars, create an online pair, watch it appear, pause it, re-activate
// it, run it (mirroring through the recording Graph provider), then delete it and confirm it
// is gone. Every step asserts the user-visible outcome before the next step runs.
public class PanelUserJourneyTests
{
    [Fact]
    public async Task Panel_user_creates_pauses_runs_and_deletes_an_online_pair_end_to_end()
    {
        using var h = new E2EHarness();

        // 1. Sign in (cookie). The OAuth callback upserts the user AND writes their connected
        //    account under their user id.
        var panel = await h.SignInAsync("oid-zeze", "zeze@test", "Zeze Lazo");

        // 2. /api/me shows the signed-in identity.
        var me = await panel.GetFromJsonAsync<JsonElement>("/api/me");
        me.GetProperty("email").GetString().Should().Be("zeze@test");
        me.GetProperty("displayName").GetString().Should().Be("Zeze Lazo");

        // 3. /api/accounts shows the account the callback connected for this user.
        var accounts = await panel.GetFromJsonAsync<JsonElement>("/api/accounts");
        accounts.GetArrayLength().Should().Be(1);
        accounts[0].GetProperty("accountRef").GetString().Should().Be("zeze@test");
        accounts[0].GetProperty("isDefault").GetBoolean().Should().BeTrue();

        // 4. /api/accounts/{ref}/calendars enumerates that account's calendars via the writer.
        var cals = await panel.GetFromJsonAsync<JsonElement>("/api/accounts/zeze@test/calendars");
        cals.GetArrayLength().Should().Be(1);
        cals[0].GetProperty("id").GetString().Should().Be("dst-cal");

        // 5. Create an online (Graph->Graph) pair into the connected account.
        var pairId = await E2EHarness.CreatePairAsync(
            panel, E2EHarness.OnlinePairBody("Work mirror", "zeze@test", "zeze@test"));

        // 6. /api/pairs lists exactly the new pair, active.
        var list = await panel.GetFromJsonAsync<JsonElement>("/api/pairs");
        list.GetArrayLength().Should().Be(1);
        list[0].GetProperty("id").GetString().Should().Be(pairId);
        list[0].GetProperty("state").GetString().Should().Be("active");
        list[0].GetProperty("name").GetString().Should().Be("Work mirror");

        // 7. PATCH -> paused, then PATCH -> active, each reflected on read-back.
        var paused = await panel.PatchAsJsonAsync($"/api/pairs/{pairId}", new { state = "paused" });
        paused.StatusCode.Should().Be(HttpStatusCode.OK);
        (await panel.GetFromJsonAsync<JsonElement>($"/api/pairs/{pairId}"))
            .GetProperty("state").GetString().Should().Be("paused");

        var reactivated = await panel.PatchAsJsonAsync($"/api/pairs/{pairId}", new { state = "active", intervalMin = 30 });
        reactivated.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterReactivate = await panel.GetFromJsonAsync<JsonElement>($"/api/pairs/{pairId}");
        afterReactivate.GetProperty("state").GetString().Should().Be("active");
        afterReactivate.GetProperty("intervalMin").GetInt32().Should().Be(30);

        // 8. Run the pair from the panel. The recording reader yields two events; the writer
        //    mirrors them into the destination account and the result is recorded on the pair.
        h.ReaderWindow.AddRange(new[] { E2EHarness.Event("a"), E2EHarness.Event("b") });
        var run = await panel.PostAsync($"/api/pairs/{pairId}/run", null);

        // /run accepts the cookie scheme too (the panel's per-pair "Sync now"): a signed-in
        // cookie client succeeds rather than being kicked to the sign-in gate. The pair is
        // loaded user-scoped, so this is safe.
        run.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var runDoc = JsonDocument.Parse(await run.Content.ReadAsStringAsync()))
            runDoc.RootElement.GetProperty("created").GetInt32().Should().Be(2);

        // 9. Delete the pair -> gone (204), and a subsequent GET is 404.
        var del = await panel.DeleteAsync($"/api/pairs/{pairId}");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await panel.GetAsync($"/api/pairs/{pairId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await panel.GetFromJsonAsync<JsonElement>("/api/pairs")).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Panel_pair_run_executes_via_an_authorized_device_and_mirrors_the_window()
    {
        using var h = new E2EHarness();

        // The panel user owns an online pair...
        var panel = await h.SignInAsync("oid-zeze", "zeze@test", "Zeze Lazo");
        var pairId = await E2EHarness.CreatePairAsync(
            panel, E2EHarness.OnlinePairBody("Work mirror", "zeze@test", "zeze@test"));

        // ...and a device bound to the same user runs it (the run/push surface is api-key gated).
        var userId = await h.UserIdAsync("oid-zeze", "zeze@test", "Zeze Lazo");
        var device = h.DeviceClient(await h.AddDeviceForUserAsync(userId));

        h.ReaderWindow.AddRange(new[] { E2EHarness.Event("a"), E2EHarness.Event("b"), E2EHarness.Event("c") });

        var run = await device.PostAsync($"/api/pairs/{pairId}/run", null);
        run.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await run.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("created").GetInt32().Should().Be(3);

        // The reader read the source account and the writer mirrored into the destination
        // account — both resolved with the user's own account ref.
        h.ReaderAccountRefs.Should().ContainSingle().Which.Should().Be("zeze@test");
        h.WriterAccountRefs.Should().ContainSingle().Which.Should().Be("zeze@test");
        h.MirroredBatches.Should().ContainSingle().Which.Select(e => e.Id)
            .Should().BeEquivalentTo(new[] { "a", "b", "c" });

        // The run result is persisted on the pair and visible to the panel on next read.
        var pair = await panel.GetFromJsonAsync<JsonElement>($"/api/pairs/{pairId}");
        pair.GetProperty("lastResult").GetProperty("created").GetInt32().Should().Be(3);
        pair.GetProperty("lastRunUtc").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Unauthenticated_panel_calls_are_gated_then_open_after_sign_in()
    {
        using var h = new E2EHarness();

        // Before sign-in the panel session does not exist: the cookie-gated surface answers 401
        // (the panel renders its sign-in gate on this), never a redirect to a login page.
        var anon = h.Factory.CreateClient();
        (await anon.GetAsync("/api/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.GetAsync("/api/accounts")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anon.GetAsync("/api/pairs")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // After the real sign-in flow the same surface opens up for the now-authenticated user.
        var panel = await h.SignInAsync("oid-zeze", "zeze@test", "Zeze Lazo");
        (await panel.GetAsync("/api/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await panel.GetAsync("/api/pairs")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
