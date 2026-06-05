using System.Collections.Generic;
using FluentAssertions;
using ZyncMaster.Engine;
using Xunit;

namespace ZyncMaster.Engine.Tests;

// Feature 2 — PairRunner.ResolveComSelection picks the COM calendar selection for a pair:
//   AllCalendars => null ("all"); explicit CalendarNames => those; legacy => the device default.
public sealed class PairRunnerSelectionTests
{
    private static SyncPair PairWithSource(Endpoint source) => new()
    {
        Id = "p1", Name = "Pair", Source = source,
        Destination = new Endpoint { Provider = "MicrosoftGraph", CalendarId = "dst" },
        IntervalMin = 15, State = "active",
    };

    private static EngineSettings Settings(IReadOnlyList<string>? deviceNames) =>
        new EngineSettings { ServerBaseUrl = "https://s.test", CalendarNames = deviceNames };

    [Fact]
    public void AllCalendars_ReturnsNull_MeaningAll()
    {
        var pair = PairWithSource(new Endpoint { Provider = "OutlookCom", CalendarId = "local", AllCalendars = true });

        PairRunner.ResolveComSelection(pair, Settings(new[] { "Ignored" })).Should().BeNull();
    }

    [Fact]
    public void ExplicitCalendarNames_AreUsed()
    {
        var pair = PairWithSource(new Endpoint
        {
            Provider = "OutlookCom", CalendarId = "local",
            AllCalendars = false, CalendarNames = new[] { "Work", "Personal" },
        });

        PairRunner.ResolveComSelection(pair, Settings(new[] { "DeviceDefault" }))
            .Should().BeEquivalentTo(new[] { "Work", "Personal" });
    }

    [Fact]
    public void Legacy_NoSelection_FallsBackToDeviceDefault()
    {
        var pair = PairWithSource(new Endpoint { Provider = "OutlookCom", CalendarId = "local" });

        PairRunner.ResolveComSelection(pair, Settings(new[] { "DeviceDefault" }))
            .Should().BeEquivalentTo(new[] { "DeviceDefault" });
    }

    [Fact]
    public void Legacy_NoSelectionAndNoDeviceDefault_ReturnsNull()
    {
        var pair = PairWithSource(new Endpoint { Provider = "OutlookCom", CalendarId = "local" });

        PairRunner.ResolveComSelection(pair, Settings(null)).Should().BeNull();
    }
}
