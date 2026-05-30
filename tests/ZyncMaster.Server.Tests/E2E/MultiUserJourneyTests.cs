using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using ZyncMaster.Core;
using Xunit;

namespace ZyncMaster.Server.Tests.E2E;

// Two FULL user journeys interleaved on the same live host, asserting zero cross-visibility
// end-to-end. This complements the endpoint-level CrossUserIsolationTests by weaving both
// users' whole journeys together (sign in, connect, create, run, list) step by step rather
// than checking isolation on individual endpoints in isolation.
public class MultiUserJourneyTests
{
    [Fact]
    public async Task Two_users_run_full_journeys_interleaved_with_zero_cross_visibility()
    {
        using var h = new E2EHarness();

        // --- Alice signs in and sets up her pair ---
        var alice = await h.SignInAsync("oid-alice", "alice@test", "Alice");
        var aliceId = await h.UserIdAsync("oid-alice", "alice@test", "Alice");
        var alicePair = await E2EHarness.CreatePairAsync(
            alice, E2EHarness.OnlinePairBody("Alice mirror", "alice@test", "alice@test"));

        // --- Bob signs in (same host) and sets up his own pair ---
        var bob = await h.SignInAsync("oid-bob", "bob@test", "Bob");
        var bobId = await h.UserIdAsync("oid-bob", "bob@test", "Bob");
        var bobPair = await E2EHarness.CreatePairAsync(
            bob, E2EHarness.OnlinePairBody("Bob mirror", "bob@test", "bob@test"));

        // --- /api/me and /api/accounts are each scoped to the caller ---
        (await alice.GetFromJsonAsync<JsonElement>("/api/me")).GetProperty("email").GetString().Should().Be("alice@test");
        (await bob.GetFromJsonAsync<JsonElement>("/api/me")).GetProperty("email").GetString().Should().Be("bob@test");

        // --- Each user's pair list contains only their own pair ---
        var aliceList = await alice.GetFromJsonAsync<JsonElement>("/api/pairs");
        aliceList.GetArrayLength().Should().Be(1);
        aliceList[0].GetProperty("id").GetString().Should().Be(alicePair);

        var bobList = await bob.GetFromJsonAsync<JsonElement>("/api/pairs");
        bobList.GetArrayLength().Should().Be(1);
        bobList[0].GetProperty("id").GetString().Should().Be(bobPair);

        // --- Cross reads/writes are 404, never 403/500: no existence leak ---
        (await alice.GetAsync($"/api/pairs/{bobPair}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await bob.GetAsync($"/api/pairs/{alicePair}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await alice.PatchAsJsonAsync($"/api/pairs/{bobPair}", new { name = "Hijack" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await bob.DeleteAsync($"/api/pairs/{alicePair}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // --- Each user's device runs ONLY their own pair, mirroring to their own account ---
        var aliceDevice = h.DeviceClient(await h.AddDeviceForUserAsync(aliceId, "Alice-Laptop"));
        var bobDevice = h.DeviceClient(await h.AddDeviceForUserAsync(bobId, "Bob-Phone"));

        h.ReaderWindow.Add(E2EHarness.Event("e"));

        // Alice's device cannot touch Bob's pair.
        (await aliceDevice.PostAsync($"/api/pairs/{bobPair}/run", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Alice's device runs Alice's pair into Alice's account.
        (await aliceDevice.PostAsync($"/api/pairs/{alicePair}/run", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        h.WriterAccountRefs.Should().ContainSingle().Which.Should().Be("alice@test");

        // Bob's device runs Bob's pair into Bob's account (now two writer resolutions total).
        (await bobDevice.PostAsync($"/api/pairs/{bobPair}/run", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        h.WriterAccountRefs.Should().BeEquivalentTo(new[] { "alice@test", "bob@test" });

        // --- After everything, each user still sees exactly one pair: their own, untouched name ---
        var aliceFinal = await alice.GetFromJsonAsync<JsonElement>("/api/pairs");
        aliceFinal.GetArrayLength().Should().Be(1);
        aliceFinal[0].GetProperty("name").GetString().Should().Be("Alice mirror");

        var bobFinal = await bob.GetFromJsonAsync<JsonElement>("/api/pairs");
        bobFinal.GetArrayLength().Should().Be(1);
        bobFinal[0].GetProperty("name").GetString().Should().Be("Bob mirror");
    }
}
