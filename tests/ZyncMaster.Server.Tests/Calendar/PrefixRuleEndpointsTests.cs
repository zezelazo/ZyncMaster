using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using static ZyncMaster.Server.Tests.Calendar.CalendarV2TestHelper;

namespace ZyncMaster.Server.Tests.Calendar;

public class PrefixRuleEndpointsTests
{
    private static object RuleBody(string prefix = "Lunch", string maskTitle = "Lunch") => new
    {
        prefix,
        maskTitle,
        enabled = true,
        sortOrder = 0,
        destinations = new[] { new { accountId = "acc-dst", calendarId = "cal-1" } },
    };

    private static (Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory,
        string Token, string UserId) Setup()
    {
        var factory = CreateFactory(new Dictionary<string, FakeReplicaClient>
        {
            ["acc-dst"] = new FakeReplicaClient(),
        });
        var (token, userId) = IssueBearer(factory, "alice", "alice@test");
        SeedAccount(factory, userId, "acc-dst");
        return (factory, token, userId);
    }

    private static async Task<string> CreateRuleAsync(HttpClient client, string token)
    {
        var response = await client.SendAsync(Bearer(HttpMethod.Post,
            "/api/calendar/prefix-rules", token, RuleBody()));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Create_then_list_round_trips_the_rule_with_destinations()
    {
        var (factory, token, _) = Setup();
        using var f = factory;
        var client = f.CreateClient();

        var id = await CreateRuleAsync(client, token);

        var list = await client.SendAsync(Bearer(HttpMethod.Get, "/api/calendar/prefix-rules", token));
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement;
        json.GetArrayLength().Should().Be(1);
        json[0].GetProperty("id").GetString().Should().Be(id);
        json[0].GetProperty("prefix").GetString().Should().Be("Lunch");
        json[0].GetProperty("destinations").GetArrayLength().Should().Be(1);
        json[0].GetProperty("destinations")[0].GetProperty("calendarId").GetString().Should().Be("cal-1");
    }

    [Fact]
    public async Task Update_replaces_fields_and_destinations()
    {
        var (factory, token, _) = Setup();
        using var f = factory;
        var client = f.CreateClient();
        var id = await CreateRuleAsync(client, token);

        var response = await client.SendAsync(Bearer(HttpMethod.Put,
            $"/api/calendar/prefix-rules/{id}", token, new
            {
                prefix = "Gym",
                maskTitle = "Workout",
                enabled = false,
                sortOrder = 3,
                destinations = new[] { new { accountId = "acc-dst", calendarId = "cal-2" } },
            }));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await client.SendAsync(Bearer(HttpMethod.Get, "/api/calendar/prefix-rules", token));
        var json = JsonDocument.Parse(await list.Content.ReadAsStringAsync()).RootElement;
        json[0].GetProperty("prefix").GetString().Should().Be("Gym");
        json[0].GetProperty("enabled").GetBoolean().Should().BeFalse();
        json[0].GetProperty("destinations")[0].GetProperty("calendarId").GetString().Should().Be("cal-2");
    }

    [Fact]
    public async Task Delete_removes_the_rule()
    {
        var (factory, token, _) = Setup();
        using var f = factory;
        var client = f.CreateClient();
        var id = await CreateRuleAsync(client, token);

        (await client.SendAsync(Bearer(HttpMethod.Delete, $"/api/calendar/prefix-rules/{id}", token)))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = await client.SendAsync(Bearer(HttpMethod.Get, "/api/calendar/prefix-rules", token));
        JsonDocument.Parse(await list.Content.ReadAsStringAsync())
            .RootElement.GetArrayLength().Should().Be(0);
    }

    [Theory]
    [InlineData("", "Lunch")]      // empty prefix
    [InlineData("Lunch", "")]      // empty mask title
    [InlineData("[Lunch]", "L")]   // brackets are syntax, not part of the prefix
    public async Task Invalid_rules_are_400(string prefix, string maskTitle)
    {
        var (factory, token, _) = Setup();
        using var f = factory;
        var client = f.CreateClient();

        var response = await client.SendAsync(Bearer(HttpMethod.Post,
            "/api/calendar/prefix-rules", token, RuleBody(prefix, maskTitle)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Destination_account_must_exist_be_graph_and_readwrite()
    {
        var (factory, token, userId) = Setup();
        using var f = factory;
        SeedAccount(f, userId, "acc-ro", scope: AccountScope.Read);
        var client = f.CreateClient();

        var unknown = await client.SendAsync(Bearer(HttpMethod.Post,
            "/api/calendar/prefix-rules", token, new
            {
                prefix = "X", maskTitle = "X", enabled = true, sortOrder = 0,
                destinations = new[] { new { accountId = "ghost", calendarId = "c" } },
            }));
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var readOnly = await client.SendAsync(Bearer(HttpMethod.Post,
            "/api/calendar/prefix-rules", token, new
            {
                prefix = "X", maskTitle = "X", enabled = true, sortOrder = 0,
                destinations = new[] { new { accountId = "acc-ro", calendarId = "c" } },
            }));
        readOnly.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "fan-out destinations need readwrite — fail at rule creation, not at 3 AM in the cron");
    }

    [Fact]
    public async Task CrossUser_rules_are_invisible_and_immutable()
    {
        var (factory, tokenA, _) = Setup();
        using var f = factory;
        var client = f.CreateClient();
        var id = await CreateRuleAsync(client, tokenA);
        var (tokenB, _) = IssueBearer(f, "bob", "bob@test");

        (await client.SendAsync(Bearer(HttpMethod.Get, "/api/calendar/prefix-rules", tokenB)))
            .Content.ReadAsStringAsync().Result.Should().Be("[]");
        (await client.SendAsync(Bearer(HttpMethod.Put, $"/api/calendar/prefix-rules/{id}", tokenB,
            RuleBody()))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.SendAsync(Bearer(HttpMethod.Delete, $"/api/calendar/prefix-rules/{id}", tokenB)))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task No_bearer_is_401()
    {
        var (factory, _, _) = Setup();
        using var f = factory;
        var client = f.CreateClient();

        (await client.SendAsync(Bearer(HttpMethod.Get, "/api/calendar/prefix-rules", null)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
