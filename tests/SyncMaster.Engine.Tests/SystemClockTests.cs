using System;
using FluentAssertions;
using SyncMaster.Engine;
using Xunit;

namespace SyncMaster.Engine.Tests;

public sealed class SystemClockTests
{
    [Fact]
    public void UtcNow_IsWithinFiveSecondsOfNow()
    {
        var before = DateTimeOffset.UtcNow;
        var actual = new SystemClock().UtcNow;
        var after = DateTimeOffset.UtcNow;

        actual.Should().BeOnOrAfter(before.AddSeconds(-5));
        actual.Should().BeOnOrBefore(after.AddSeconds(5));
    }
}
