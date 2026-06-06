using System;
using FluentAssertions;
using ZyncMaster.Server.Data;
using Xunit;

namespace ZyncMaster.Server.Tests.Sync;

// Serializes an Endpoint to the same camelCase JSON shape the server's internal PairJson writes,
// so seeded SyncPairRow.*Json columns deserialize through PairJson identically. Internal PairJson
// is not visible to the test assembly, hence this local equivalent.
internal static class TestEndpointJson
{
    public static string Serialize(Endpoint e) =>
        System.Text.Json.JsonSerializer.Serialize(
            new { provider = e.Provider, accountRef = e.AccountRef, calendarId = e.CalendarId, calendarName = e.CalendarName },
            new System.Text.Json.JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            });
}

// Unit coverage for the cron runner's pure selection predicates (the cross-user execution path is
// covered end-to-end in SyncRunDueEndpointTests).
public class CronSyncRunnerPredicateTests
{
    private static SyncPairRow Row(
        string state = "active",
        int intervalMin = 15,
        DateTimeOffset? lastRunUtc = null,
        string sourceProvider = "MicrosoftGraph",
        string destProvider = "MicrosoftGraph") => new()
    {
        Id = "p1",
        UserId = "u1",
        Name = "P",
        State = state,
        IntervalMin = intervalMin,
        LastRunUtc = lastRunUtc,
        SourceJson = TestEndpointJson.Serialize(new Endpoint { Provider = sourceProvider, AccountRef = "default", CalendarId = "s" }),
        DestinationJson = TestEndpointJson.Serialize(new Endpoint { Provider = destProvider, AccountRef = "default", CalendarId = "d" }),
    };

    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Never_run_active_pair_is_due()
    {
        CronSyncRunner.IsDue(Row(lastRunUtc: null), Now).Should().BeTrue();
    }

    [Fact]
    public void Pair_within_interval_is_not_due()
    {
        CronSyncRunner.IsDue(Row(intervalMin: 30, lastRunUtc: Now.AddMinutes(-10)), Now).Should().BeFalse();
    }

    [Fact]
    public void Pair_past_interval_is_due()
    {
        CronSyncRunner.IsDue(Row(intervalMin: 30, lastRunUtc: Now.AddMinutes(-31)), Now).Should().BeTrue();
    }

    [Fact]
    public void Non_active_pair_is_never_due()
    {
        CronSyncRunner.IsDue(Row(state: "paused", lastRunUtc: null), Now).Should().BeFalse();
        CronSyncRunner.IsDue(Row(state: "disabled", lastRunUtc: null), Now).Should().BeFalse();
    }

    [Fact]
    public void Zero_interval_is_always_due()
    {
        CronSyncRunner.IsDue(Row(intervalMin: 0, lastRunUtc: Now), Now).Should().BeTrue();
    }

    [Fact]
    public void Com_source_is_com_pinned()
    {
        // Detection is SOURCE-ONLY (the COM side is always the source; there is no COM writer), and
        // case-insensitive, matching PairEndpoints.IsComPinnedPair and PairRunner.IsOutlookCom.
        CronSyncRunner.IsComPinned(Row(sourceProvider: "OutlookCom")).Should().BeTrue();
        CronSyncRunner.IsComPinned(Row(sourceProvider: "outlookcom")).Should().BeTrue();
    }

    [Fact]
    public void Com_destination_alone_is_not_com_pinned()
    {
        // A COM destination (not a real configuration) must NOT make the pair COM-pinned: the cron
        // would otherwise wrongly skip a Graph-sourced pair that it can run server-side.
        CronSyncRunner.IsComPinned(Row(destProvider: "OutlookCom")).Should().BeFalse();
    }

    [Fact]
    public void Graph_to_graph_is_not_com_pinned()
    {
        CronSyncRunner.IsComPinned(Row()).Should().BeFalse();
    }
}
