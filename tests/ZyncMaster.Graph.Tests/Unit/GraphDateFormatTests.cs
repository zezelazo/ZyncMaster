using System;
using FluentAssertions;
using ZyncMaster.Graph;
using Xunit;

namespace ZyncMaster.Graph.Tests;

// The three formatting branches Graph payloads need (same rules as the pair mirror): all-day
// pins the LOCAL date so the day boundary never shifts, a "UTC" zone label coerces both
// instants to real UTC, and any other zone keeps the local wall-clock + the original zone id.
public class GraphDateFormatTests
{
    [Fact]
    public void AllDay_uses_the_local_date_at_midnight_and_utc_zone()
    {
        // 23:30 on June 14 at UTC-5: the LOCAL date (14th) must win, never the UTC date (15th).
        var start = new DateTimeOffset(2026, 6, 14, 23, 30, 0, TimeSpan.FromHours(-5));
        var end = new DateTimeOffset(2026, 6, 15, 23, 30, 0, TimeSpan.FromHours(-5));

        var (s, e, tz) = GraphDateFormat.For(start, end, "America/Lima", isAllDay: true);

        s.Should().Be("2026-06-14T00:00:00.0000000");
        e.Should().Be("2026-06-15T00:00:00.0000000");
        tz.Should().Be("UTC");
    }

    [Fact]
    public void AllDay_with_end_not_after_start_is_widened_to_one_full_day()
    {
        var sameDay = new DateTimeOffset(2026, 6, 14, 9, 0, 0, TimeSpan.Zero);

        var (s, e, _) = GraphDateFormat.For(sameDay, sameDay, "UTC", isAllDay: true);

        s.Should().Be("2026-06-14T00:00:00.0000000");
        e.Should().Be("2026-06-15T00:00:00.0000000", "Graph rejects all-day events whose end does not exceed the start");
    }

    [Fact]
    public void Utc_zone_label_coerces_both_instants_to_real_utc()
    {
        // Offset-bearing inputs + a "UTC" label: datetime and timeZone must agree, so the
        // wall-clock is converted to the UTC instant instead of being passed through.
        var start = new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.FromHours(2));
        var end = new DateTimeOffset(2026, 6, 15, 11, 0, 0, TimeSpan.FromHours(2));

        var (s, e, tz) = GraphDateFormat.For(start, end, "UTC", isAllDay: false);

        s.Should().Be("2026-06-15T08:00:00.0000000");
        e.Should().Be("2026-06-15T09:00:00.0000000");
        tz.Should().Be("UTC");
    }

    [Fact]
    public void Other_zone_keeps_the_local_wall_clock_and_the_original_zone_id()
    {
        var start = new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.FromHours(-5));
        var end = new DateTimeOffset(2026, 6, 15, 11, 30, 0, TimeSpan.FromHours(-5));

        var (s, e, tz) = GraphDateFormat.For(start, end, "America/Lima", isAllDay: false);

        s.Should().Be("2026-06-15T10:00:00.0000000");
        e.Should().Be("2026-06-15T11:30:00.0000000");
        tz.Should().Be("America/Lima");
    }
}
