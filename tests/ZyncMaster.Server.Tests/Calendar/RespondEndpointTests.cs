using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ZyncMaster.Graph;
using Xunit;
using static ZyncMaster.Server.Tests.Calendar.CalendarV2TestHelper;

namespace ZyncMaster.Server.Tests.Calendar;

public class RespondEndpointTests
{
    private static SourceEventSnapshot Meeting(bool organizer, bool attendees = true) => new()
    {
        GraphEventId = "ev-1",
        StableId = "stable-ev-1",
        Subject = "Planning",
        Start = new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero),
        End = new DateTimeOffset(2026, 6, 15, 11, 0, 0, TimeSpan.Zero),
        IsOrganizer = organizer,
        HasAttendees = attendees,
    };

    private static (Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory,
        string Token, string UserId, FakeReplicaClient Client, FakeResponder Responder)
        Setup(SourceEventSnapshot? snapshot,
            AccountKind kind = AccountKind.Graph, AccountScope scope = AccountScope.ReadWrite)
    {
        var fake = new FakeReplicaClient();
        if (snapshot is not null)
            fake.EventsById["ev-1"] = snapshot;
        var responder = new FakeResponder();
        var factory = CreateFactory(
            new Dictionary<string, FakeReplicaClient> { ["acc-1"] = fake },
            new Dictionary<string, FakeResponder> { ["acc-1"] = responder });
        var (token, userId) = IssueBearer(factory, "alice", "alice@test");
        SeedAccount(factory, userId, "acc-1", kind, scope);
        return (factory, token, userId, fake, responder);
    }

    [Theory]
    [InlineData("accept", RespondAction.Accept)]
    [InlineData("decline", RespondAction.Decline)]
    [InlineData("tentative", RespondAction.Tentative)]
    public async Task Respond_invokes_the_graph_action_with_the_optional_message(
        string action, RespondAction expected)
    {
        var (factory, token, _, _, responder) = Setup(Meeting(organizer: false));
        using var f = factory;
        var client = f.CreateClient();

        var response = await client.SendAsync(Bearer(HttpMethod.Post,
            "/api/calendar/events/acc-1/ev-1/respond", token,
            new { action, message = "See you next time" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        responder.Responses.Should().ContainSingle(r =>
            r.Action == expected && r.Comment == "See you next time");
    }

    [Fact]
    public async Task Cancel_as_organizer_of_a_meeting_uses_the_cancel_verb()
    {
        var (factory, token, _, fake, responder) = Setup(Meeting(organizer: true));
        using var f = factory;
        var client = f.CreateClient();

        var response = await client.SendAsync(Bearer(HttpMethod.Post,
            "/api/calendar/events/acc-1/ev-1/respond", token, new { action = "cancel" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        responder.Cancels.Should().ContainSingle();
        fake.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancel_of_a_personal_appointment_without_attendees_is_a_clean_delete()
    {
        var (factory, token, _, fake, responder) = Setup(Meeting(organizer: true, attendees: false));
        using var f = factory;
        var client = f.CreateClient();

        var response = await client.SendAsync(Bearer(HttpMethod.Post,
            "/api/calendar/events/acc-1/ev-1/respond", token, new { action = "cancel" }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Deleted.Should().ContainSingle(id => id == "ev-1",
            "nobody to notify: cancel == silent delete (the CalImport rationale)");
        responder.Cancels.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancel_when_not_the_organizer_is_409_organizer_required()
    {
        var (factory, token, _, _, _) = Setup(Meeting(organizer: false));
        using var f = factory;
        var client = f.CreateClient();

        var response = await client.SendAsync(Bearer(HttpMethod.Post,
            "/api/calendar/events/acc-1/ev-1/respond", token, new { action = "cancel" }));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("organizer_required");
    }

    [Fact]
    public async Task Com_origin_gets_the_documented_v11_deferral_never_silence()
    {
        var (factory, token, _, _, _) = Setup(null, kind: AccountKind.OutlookCom);
        using var f = factory;
        var client = f.CreateClient();

        var response = await client.SendAsync(Bearer(HttpMethod.Post,
            "/api/calendar/events/acc-1/ev-1/respond", token, new { action = "decline" }));

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("com_writeback_deferred");
        body.Should().Contain("v1.1", "the deferral must say WHEN it ships (spec §6/§13)");
    }

    [Fact]
    public async Task Read_scope_account_is_409_with_upgrade_hint()
    {
        var (factory, token, _, _, _) = Setup(Meeting(organizer: false), scope: AccountScope.Read);
        using var f = factory;
        var client = f.CreateClient();

        var response = await client.SendAsync(Bearer(HttpMethod.Post,
            "/api/calendar/events/acc-1/ev-1/respond", token, new { action = "accept" }));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("readwrite_scope_required");
    }

    [Fact]
    public async Task Confirming_writeback_closes_a_broken_link_to_tombstone()
    {
        var (factory, token, userId, fake, _) = Setup(Meeting(organizer: false));
        using var f = factory;

        // Seed a BROKEN link for this origin directly through the user-scoped store. The
        // override writes into HttpContext.Items, so out-of-request scopes need an ambient
        // HttpContext first (same trick the cron runner gets for free inside /run-due).
        string linkId;
        using (var scope = f.Services.CreateScope())
        {
            var http = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
            http.HttpContext = new DefaultHttpContext();
            var overrideUser = scope.ServiceProvider.GetRequiredService<IHttpCurrentUserOverride>();
            overrideUser.Set(userId);
            try
            {
                var links = scope.ServiceProvider.GetRequiredService<IReplicaLinkStore>();
                var link = links.AddAsync(new ReplicaLink
                {
                    Id = Guid.NewGuid().ToString("N"),
                    SourceAccountId = "acc-1",
                    SourceEventId = "stable-ev-1",
                    SourceGraphEventId = "ev-1",
                    DestinationAccountId = "acc-1",
                    DestinationCalendarId = "cal-2",
                    DestinationEventId = "gone",
                    MaskTitle = "Busy",
                    Status = ReplicaLinkStatus.Broken,
                }).GetAwaiter().GetResult();
                linkId = link.Id;
            }
            finally { overrideUser.Clear(); http.HttpContext = null; }
        }

        var client = f.CreateClient();
        var response = await client.SendAsync(Bearer(HttpMethod.Post,
            "/api/calendar/events/acc-1/ev-1/respond", token,
            new { action = "decline", message = "won't attend", linkId }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var scope2 = f.Services.CreateScope();
        var http2 = scope2.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        http2.HttpContext = new DefaultHttpContext();
        var overrideUser2 = scope2.ServiceProvider.GetRequiredService<IHttpCurrentUserOverride>();
        overrideUser2.Set(userId);
        try
        {
            var links2 = scope2.ServiceProvider.GetRequiredService<IReplicaLinkStore>();
            links2.GetAsync(linkId).GetAwaiter().GetResult()!
                .Status.Should().Be(ReplicaLinkStatus.Tombstone,
                    "confirming the write-back is one of the only two broken->tombstone flows");
        }
        finally { overrideUser2.Clear(); http2.HttpContext = null; }
    }

    [Fact]
    public async Task Unknown_event_is_404_and_invalid_action_is_400()
    {
        var (factory, token, _, _, _) = Setup(null);
        using var f = factory;
        var client = f.CreateClient();

        (await client.SendAsync(Bearer(HttpMethod.Post,
            "/api/calendar/events/acc-1/missing/respond", token, new { action = "accept" })))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await client.SendAsync(Bearer(HttpMethod.Post,
            "/api/calendar/events/acc-1/ev-1/respond", token, new { action = "shrug" })))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CrossUser_account_is_404_and_no_bearer_is_401()
    {
        var (factory, _, _, _, _) = Setup(Meeting(organizer: false));
        using var f = factory;
        var (tokenB, _) = IssueBearer(f, "bob", "bob@test");
        var client = f.CreateClient();

        (await client.SendAsync(Bearer(HttpMethod.Post,
            "/api/calendar/events/acc-1/ev-1/respond", tokenB, new { action = "accept" })))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await client.SendAsync(Bearer(HttpMethod.Post,
            "/api/calendar/events/acc-1/ev-1/respond", null, new { action = "accept" })))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
