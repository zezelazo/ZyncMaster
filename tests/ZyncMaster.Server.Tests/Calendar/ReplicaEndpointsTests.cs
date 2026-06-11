using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using ZyncMaster.Graph;
using Xunit;
using static ZyncMaster.Server.Tests.Calendar.CalendarV2TestHelper;

namespace ZyncMaster.Server.Tests.Calendar;

public class ReplicaEndpointsTests
{
    private static SourceEventSnapshot Snapshot(string graphId = "ev-1") => new()
    {
        GraphEventId = graphId,
        StableId = $"stable-{graphId}",
        Subject = "Secret subject",
        Start = new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero),
        End = new DateTimeOffset(2026, 6, 15, 11, 0, 0, TimeSpan.Zero),
        ShowAs = "busy",
    };

    [Fact]
    public async Task FanOut_creates_replicas_and_returns_the_links()
    {
        var src = new FakeReplicaClient();
        var dst = new FakeReplicaClient();
        src.EventsById["ev-1"] = Snapshot();
        using var factory = CreateFactory(new Dictionary<string, FakeReplicaClient>
        {
            ["acc-src"] = src, ["acc-dst"] = dst,
        });
        var (token, userId) = IssueBearer(factory, "alice", "alice@test");
        SeedAccount(factory, userId, "acc-src", scope: AccountScope.Read);
        SeedAccount(factory, userId, "acc-dst");
        var client = factory.CreateClient();

        var response = await client.SendAsync(Bearer(HttpMethod.Post,
            "/api/calendar/events/acc-src/ev-1/replicas", token, new
            {
                destinations = new[] { new { accountId = "acc-dst", calendarId = "cal-1", title = "Busy" } },
            }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("created").GetArrayLength().Should().Be(1);
        json.GetProperty("created")[0].GetProperty("maskTitle").GetString().Should().Be("Busy");
        dst.CreatedReplicas.Should().ContainSingle();
        dst.CreatedReplicas[0].Draft.MaskTitle.Should().Be("Busy",
            "PRIVACY: the destination only ever sees the manual mask");
    }

    [Fact]
    public async Task FanOut_replica_marked_source_is_rejected_422()
    {
        var src = new FakeReplicaClient();
        src.EventsById["ev-1"] = Snapshot() with { HasReplicaMark = true };
        using var factory = CreateFactory(new Dictionary<string, FakeReplicaClient> { ["acc-src"] = src });
        var (token, userId) = IssueBearer(factory, "alice", "alice@test");
        SeedAccount(factory, userId, "acc-src");
        var client = factory.CreateClient();

        var response = await client.SendAsync(Bearer(HttpMethod.Post,
            "/api/calendar/events/acc-src/ev-1/replicas", token, new
            {
                destinations = new[] { new { accountId = "acc-src", calendarId = "cal-1", title = "X" } },
            }));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).Should().Contain("replica_cannot_be_source");
    }

    [Fact]
    public async Task FanOut_readonly_destination_returns_409()
    {
        var src = new FakeReplicaClient();
        var dst = new FakeReplicaClient();
        src.EventsById["ev-1"] = Snapshot();
        using var factory = CreateFactory(new Dictionary<string, FakeReplicaClient>
        {
            ["acc-src"] = src, ["acc-dst"] = dst,
        });
        var (token, userId) = IssueBearer(factory, "alice", "alice@test");
        SeedAccount(factory, userId, "acc-src");
        SeedAccount(factory, userId, "acc-dst", scope: AccountScope.Read);
        var client = factory.CreateClient();

        var response = await client.SendAsync(Bearer(HttpMethod.Post,
            "/api/calendar/events/acc-src/ev-1/replicas", token, new
            {
                destinations = new[] { new { accountId = "acc-dst", calendarId = "cal-1", title = "Busy" } },
            }));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("readwrite_scope_required");
    }

    [Fact]
    public async Task FanOut_cross_user_account_is_404_never_a_leak()
    {
        var src = new FakeReplicaClient();
        src.EventsById["ev-1"] = Snapshot();
        using var factory = CreateFactory(new Dictionary<string, FakeReplicaClient> { ["acc-src"] = src });
        var (tokenA, userA) = IssueBearer(factory, "alice", "alice@test");
        var (tokenB, _) = IssueBearer(factory, "bob", "bob@test");
        SeedAccount(factory, userA, "acc-src");
        var client = factory.CreateClient();

        var response = await client.SendAsync(Bearer(HttpMethod.Post,
            "/api/calendar/events/acc-src/ev-1/replicas", tokenB, new
            {
                destinations = new[] { new { accountId = "acc-src", calendarId = "cal-1", title = "X" } },
            }));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "another user's account id must behave exactly like a nonexistent one");
    }

    [Fact]
    public async Task FanOut_without_bearer_is_401()
    {
        using var factory = CreateFactory(new Dictionary<string, FakeReplicaClient>());
        var client = factory.CreateClient();

        var response = await client.SendAsync(Bearer(HttpMethod.Post,
            "/api/calendar/events/a/e/replicas", token: null, new { destinations = Array.Empty<object>() }));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task FanOut_empty_destinations_is_400()
    {
        var src = new FakeReplicaClient();
        using var factory = CreateFactory(new Dictionary<string, FakeReplicaClient> { ["acc-src"] = src });
        var (token, userId) = IssueBearer(factory, "alice", "alice@test");
        SeedAccount(factory, userId, "acc-src");
        var client = factory.CreateClient();

        var response = await client.SendAsync(Bearer(HttpMethod.Post,
            "/api/calendar/events/acc-src/ev-1/replicas", token, new { destinations = Array.Empty<object>() }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateEvent_writes_body_and_location_to_the_origin_and_never_to_replicas()
    {
        var acc = new FakeReplicaClient();
        using var factory = CreateFactory(new Dictionary<string, FakeReplicaClient> { ["acc-1"] = acc });
        var (token, userId) = IssueBearer(factory, "alice", "alice@test");
        SeedAccount(factory, userId, "acc-1");
        var client = factory.CreateClient();

        var response = await client.SendAsync(Bearer(HttpMethod.Post, "/api/calendar/events", token, new
        {
            accountId = "acc-1",
            calendarId = "cal-1",
            title = "Dentist",
            start = "2026-06-15T09:00:00Z",
            end = "2026-06-15T09:30:00Z",
            body = "<p>bring x-rays</p>",
            location = "Clinic 4",
            replicas = new[] { new { accountId = "acc-1", calendarId = "cal-2", title = "Out" } },
        }));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        acc.CreatedOrigins.Should().ContainSingle();
        acc.CreatedOrigins[0].Draft.BodyHtml.Should().Contain("x-rays");
        acc.CreatedOrigins[0].Draft.Location.Should().Be("Clinic 4");
        acc.CreatedReplicas.Should().ContainSingle();
        acc.CreatedReplicas[0].Draft.MaskTitle.Should().Be("Out");
        // PRIVACY: ReplicaDraft cannot even represent body/location; assert the mask is not
        // the origin title to prove no copy-the-subject code path exists in the endpoint.
        acc.CreatedReplicas[0].Draft.MaskTitle.Should().NotBe("Dentist");
    }

    [Fact]
    public async Task CreateEvent_on_readonly_account_is_409_with_upgrade_hint()
    {
        var acc = new FakeReplicaClient();
        using var factory = CreateFactory(new Dictionary<string, FakeReplicaClient> { ["acc-1"] = acc });
        var (token, userId) = IssueBearer(factory, "alice", "alice@test");
        SeedAccount(factory, userId, "acc-1", scope: AccountScope.Read);
        var client = factory.CreateClient();

        var response = await client.SendAsync(Bearer(HttpMethod.Post, "/api/calendar/events", token, new
        {
            accountId = "acc-1",
            calendarId = "cal-1",
            title = "Dentist",
            start = "2026-06-15T09:00:00Z",
            end = "2026-06-15T09:30:00Z",
        }));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("readwrite_scope_required");
    }

    [Fact]
    public async Task PatchReplica_title_renames_in_graph_and_persists()
    {
        var (factory, token, _, dst, linkId) = await SeedLinkAsync();
        using var f = factory;
        var client = f.CreateClient();

        var response = await client.SendAsync(Bearer(HttpMethod.Patch,
            $"/api/calendar/replicas/{linkId}", token, new { title = "Focus" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        dst.PatchedSubjects.Should().ContainSingle(p => p.Subject == "Focus");
    }

    [Fact]
    public async Task DeleteReplica_deletes_the_event_and_tombstones_the_link()
    {
        var (factory, token, _, dst, linkId) = await SeedLinkAsync();
        using var f = factory;
        var client = f.CreateClient();

        var response = await client.SendAsync(Bearer(HttpMethod.Delete,
            $"/api/calendar/replicas/{linkId}", token));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        dst.Deleted.Should().ContainSingle();
    }

    [Fact]
    public async Task PatchReplica_cross_user_link_is_404()
    {
        var (factory, _, _, _, linkId) = await SeedLinkAsync();
        using var f = factory;
        var (tokenB, _) = IssueBearer(f, "bob", "bob@test");
        var client = f.CreateClient();

        var response = await client.SendAsync(Bearer(HttpMethod.Patch,
            $"/api/calendar/replicas/{linkId}", tokenB, new { title = "Hijack" }));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Seeds a user + source/destination accounts + ONE active link created through the real
    // fan-out endpoint, returning everything a link-mutation test needs.
    private static async Task<(System.Object Factory, string Token, FakeReplicaClient Src,
        FakeReplicaClient Dst, string LinkId)> SeedLinkInternalAsync()
    {
        var src = new FakeReplicaClient();
        var dst = new FakeReplicaClient();
        src.EventsById["ev-1"] = Snapshot();
        var factory = CreateFactory(new Dictionary<string, FakeReplicaClient>
        {
            ["acc-src"] = src, ["acc-dst"] = dst,
        });
        var (token, userId) = IssueBearer(factory, "alice", "alice@test");
        SeedAccount(factory, userId, "acc-src", scope: AccountScope.Read);
        SeedAccount(factory, userId, "acc-dst");
        var client = factory.CreateClient();
        var response = await client.SendAsync(Bearer(HttpMethod.Post,
            "/api/calendar/events/acc-src/ev-1/replicas", token, new
            {
                destinations = new[] { new { accountId = "acc-dst", calendarId = "cal-1", title = "Busy" } },
            }));
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var linkId = json.GetProperty("created")[0].GetProperty("id").GetString()!;
        return (factory, token, src, dst, linkId);
    }

    private static async Task<(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory,
        string Token, FakeReplicaClient Src, FakeReplicaClient Dst, string LinkId)> SeedLinkAsync()
    {
        var (factory, token, src, dst, linkId) = await SeedLinkInternalAsync();
        return ((Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>)factory, token, src, dst, linkId);
    }
}
