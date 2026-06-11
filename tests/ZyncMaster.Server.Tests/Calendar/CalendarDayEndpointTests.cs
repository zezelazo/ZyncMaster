using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ZyncMaster.Graph;
using Xunit;
using static ZyncMaster.Server.Tests.Calendar.CalendarV2TestHelper;

namespace ZyncMaster.Server.Tests.Calendar;

public class CalendarDayEndpointTests
{
    private static SourceEventSnapshot Event(string graphId, string subject,
        bool replicaMark = false) => new()
    {
        GraphEventId = graphId,
        StableId = $"stable-{graphId}",
        Subject = subject,
        Start = new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero),
        End = new DateTimeOffset(2026, 6, 15, 11, 0, 0, TimeSpan.Zero),
        ShowAs = "busy",
        HasReplicaMark = replicaMark,
    };

    [Fact]
    public async Task Day_aggregates_graph_accounts_with_replica_annotations_and_canWrite()
    {
        var acc = new FakeReplicaClient();
        acc.Calendars.Add(new CalendarTargetInfo { Id = "cal-1", DisplayName = "Main" });
        acc.WindowEvents.Add(Event("ev-1", "Standup"));
        acc.WindowEvents.Add(Event("ev-2", "Mirror copy", replicaMark: true));
        using var factory = CreateFactory(new Dictionary<string, FakeReplicaClient> { ["acc-1"] = acc });
        var (token, userId) = IssueBearer(factory, "alice", "alice@test");
        SeedAccount(factory, userId, "acc-1");

        // A link from ev-1 so the day view annotates it with its replica + mask. The override
        // writes into HttpContext.Items, so this out-of-request scope needs an ambient
        // HttpContext first (same pattern as RespondEndpointTests).
        using (var scope = factory.Services.CreateScope())
        {
            var http = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
            http.HttpContext = new DefaultHttpContext();
            var overrideUser = scope.ServiceProvider.GetRequiredService<IHttpCurrentUserOverride>();
            overrideUser.Set(userId);
            try
            {
                await scope.ServiceProvider.GetRequiredService<IReplicaLinkStore>().AddAsync(new ReplicaLink
                {
                    Id = "link-1", SourceAccountId = "acc-1", SourceEventId = "stable-ev-1",
                    SourceGraphEventId = "ev-1", DestinationAccountId = "acc-1",
                    DestinationCalendarId = "cal-2", DestinationEventId = "rep-1",
                    MaskTitle = "Busy",
                });
            }
            finally { overrideUser.Clear(); http.HttpContext = null; }
        }

        var client = factory.CreateClient();
        var response = await client.SendAsync(Bearer(HttpMethod.Get,
            "/api/calendar/day?date=2026-06-15", token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("date").GetString().Should().Be("2026-06-15");
        var account = json.GetProperty("accounts")[0];
        account.GetProperty("freshness").GetString().Should().Be("live");
        var events = account.GetProperty("events");
        events.GetArrayLength().Should().Be(2);

        var standup = events[0];
        standup.GetProperty("title").GetString().Should().Be("Standup");
        standup.GetProperty("canWrite").GetBoolean().Should().BeTrue();
        standup.GetProperty("isReplica").GetBoolean().Should().BeFalse();
        standup.GetProperty("replicas")[0].GetProperty("maskTitle").GetString().Should().Be("Busy");
        standup.GetProperty("replicas")[0].GetProperty("linkId").GetString().Should().Be("link-1");

        events[1].GetProperty("isReplica").GetBoolean().Should().BeTrue(
            "an event carrying a managed mark renders as replica (and the UI offers no re-replicate)");
    }

    [Fact]
    public async Task Read_scope_account_reports_canWrite_false_for_the_ui_degradation()
    {
        var acc = new FakeReplicaClient();
        acc.Calendars.Add(new CalendarTargetInfo { Id = "cal-1", DisplayName = "Main" });
        acc.WindowEvents.Add(Event("ev-1", "Standup"));
        using var factory = CreateFactory(new Dictionary<string, FakeReplicaClient> { ["acc-1"] = acc });
        var (token, userId) = IssueBearer(factory, "alice", "alice@test");
        SeedAccount(factory, userId, "acc-1", scope: AccountScope.Read);
        var client = factory.CreateClient();

        var response = await client.SendAsync(Bearer(HttpMethod.Get,
            "/api/calendar/day?date=2026-06-15", token));

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("accounts")[0].GetProperty("scope").GetString().Should().Be("Read");
        json.GetProperty("accounts")[0].GetProperty("events")[0]
            .GetProperty("canWrite").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Com_account_appears_with_snapshot_unavailable_never_omitted()
    {
        using var factory = CreateFactory(new Dictionary<string, FakeReplicaClient>());
        var (token, userId) = IssueBearer(factory, "alice", "alice@test");
        SeedAccount(factory, userId, "acc-com", kind: AccountKind.OutlookCom);
        var client = factory.CreateClient();

        var response = await client.SendAsync(Bearer(HttpMethod.Get,
            "/api/calendar/day?date=2026-06-15", token));

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var account = json.GetProperty("accounts")[0];
        account.GetProperty("kind").GetString().Should().Be("com");
        account.GetProperty("freshness").GetString().Should().Be("snapshot_unavailable",
            "visible degradation, never silence (plan decision 3)");
        account.GetProperty("events").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Accounts_filter_limits_the_aggregation()
    {
        var a = new FakeReplicaClient();
        var b = new FakeReplicaClient();
        a.Calendars.Add(new CalendarTargetInfo { Id = "cal-a", DisplayName = "A" });
        b.Calendars.Add(new CalendarTargetInfo { Id = "cal-b", DisplayName = "B" });
        using var factory = CreateFactory(new Dictionary<string, FakeReplicaClient>
        {
            ["acc-a"] = a, ["acc-b"] = b,
        });
        var (token, userId) = IssueBearer(factory, "alice", "alice@test");
        SeedAccount(factory, userId, "acc-a");
        SeedAccount(factory, userId, "acc-b");
        var client = factory.CreateClient();

        var response = await client.SendAsync(Bearer(HttpMethod.Get,
            "/api/calendar/day?date=2026-06-15&accounts=acc-a", token));

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        json.GetProperty("accounts").GetArrayLength().Should().Be(1);
        json.GetProperty("accounts")[0].GetProperty("accountId").GetString().Should().Be("acc-a");
    }

    [Fact]
    public async Task CrossUser_day_only_ever_sees_the_callers_accounts()
    {
        var acc = new FakeReplicaClient();
        acc.Calendars.Add(new CalendarTargetInfo { Id = "cal-1", DisplayName = "Main" });
        using var factory = CreateFactory(new Dictionary<string, FakeReplicaClient> { ["acc-1"] = acc });
        var (_, userA) = IssueBearer(factory, "alice", "alice@test");
        var (tokenB, _) = IssueBearer(factory, "bob", "bob@test");
        SeedAccount(factory, userA, "acc-1");
        var client = factory.CreateClient();

        var response = await client.SendAsync(Bearer(HttpMethod.Get,
            "/api/calendar/day?date=2026-06-15", tokenB));

        JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("accounts").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Invalid_or_missing_date_is_400_and_no_bearer_is_401()
    {
        using var factory = CreateFactory(new Dictionary<string, FakeReplicaClient>());
        var (token, _) = IssueBearer(factory, "alice", "alice@test");
        var client = factory.CreateClient();

        (await client.SendAsync(Bearer(HttpMethod.Get, "/api/calendar/day?date=15-06-2026", token)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.SendAsync(Bearer(HttpMethod.Get, "/api/calendar/day", token)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.SendAsync(Bearer(HttpMethod.Get, "/api/calendar/day?date=2026-06-15", null)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
