using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace ZyncMaster.Core.Tests;

public sealed class DailyFileLoggerTests : IDisposable
{
    private readonly string _dir;

    public DailyFileLoggerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "zynclog_" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    private static Func<DateTimeOffset> FixedClock(DateTimeOffset when) => () => when;

    [Fact]
    public void Constructor_CreatesLogDirectory()
    {
        Directory.Exists(_dir).Should().BeFalse();

        _ = new DailyFileLogger(_dir, LogLevel.Debug, FixedClock(new DateTimeOffset(2026, 6, 5, 10, 0, 0, TimeSpan.Zero)));

        Directory.Exists(_dir).Should().BeTrue();
    }

    [Fact]
    public void FileName_IsPerUtcDay()
    {
        var when = new DateTimeOffset(2026, 6, 5, 23, 59, 0, TimeSpan.Zero);
        var sut = new DailyFileLogger(_dir, LogLevel.Debug, FixedClock(when));

        sut.CurrentLogFilePath().Should().Be(Path.Combine(_dir, "zyncmaster-2026-06-05.log"));
    }

    [Fact]
    public void CrossingMidnight_RollsToNewFile()
    {
        var clockValue = new DateTimeOffset(2026, 6, 5, 23, 59, 0, TimeSpan.Zero);
        Func<DateTimeOffset> clock = () => clockValue;

        var sut = new DailyFileLogger(_dir, LogLevel.Debug, clock);
        sut.Log(LogLevel.Info, "before midnight");

        clockValue = new DateTimeOffset(2026, 6, 6, 0, 1, 0, TimeSpan.Zero);
        sut.Log(LogLevel.Info, "after midnight");

        var files = Directory.GetFiles(_dir).Select(Path.GetFileName).OrderBy(x => x).ToList();
        files.Should().BeEquivalentTo(new[] { "zyncmaster-2026-06-05.log", "zyncmaster-2026-06-06.log" });

        File.ReadAllText(Path.Combine(_dir, "zyncmaster-2026-06-05.log")).Should().Contain("before midnight");
        File.ReadAllText(Path.Combine(_dir, "zyncmaster-2026-06-06.log")).Should().Contain("after midnight");
    }

    [Fact]
    public void MinLevel_FiltersBelowThreshold()
    {
        var when = new DateTimeOffset(2026, 6, 5, 10, 0, 0, TimeSpan.Zero);
        var sut = new DailyFileLogger(_dir, LogLevel.Warning, FixedClock(when));

        sut.Log(LogLevel.Debug, "debug-line");
        sut.Log(LogLevel.Info, "info-line");
        sut.Log(LogLevel.Warning, "warning-line");
        sut.Log(LogLevel.Error, "error-line");

        var text = File.ReadAllText(sut.CurrentLogFilePath());
        text.Should().NotContain("debug-line");
        text.Should().NotContain("info-line");
        text.Should().Contain("warning-line");
        text.Should().Contain("error-line");
    }

    [Fact]
    public void IsEnabled_ReflectsMinLevel()
    {
        var sut = new DailyFileLogger(_dir, LogLevel.Warning, FixedClock(DateTimeOffset.UtcNow));

        sut.IsEnabled(LogLevel.Debug).Should().BeFalse();
        sut.IsEnabled(LogLevel.Info).Should().BeFalse();
        sut.IsEnabled(LogLevel.Warning).Should().BeTrue();
        sut.IsEnabled(LogLevel.Error).Should().BeTrue();
    }

    [Fact]
    public void Log_WritesTimestampLevelAndMessage()
    {
        var when = new DateTimeOffset(2026, 6, 5, 10, 30, 15, TimeSpan.Zero);
        var sut = new DailyFileLogger(_dir, LogLevel.Debug, FixedClock(when));

        sut.Log(LogLevel.Error, "something broke");

        var line = File.ReadAllText(sut.CurrentLogFilePath());
        line.Should().Contain("[Error]");
        line.Should().Contain("something broke");
        line.Should().Contain("2026-06-05T10:30:15");
    }

    [Fact]
    public void Log_AppendsExceptionWhenProvided()
    {
        var when = new DateTimeOffset(2026, 6, 5, 10, 0, 0, TimeSpan.Zero);
        var sut = new DailyFileLogger(_dir, LogLevel.Debug, FixedClock(when));

        sut.Log(LogLevel.Error, "failed", new InvalidOperationException("kaboom"));

        var text = File.ReadAllText(sut.CurrentLogFilePath());
        text.Should().Contain("failed");
        text.Should().Contain("kaboom");
        text.Should().Contain(nameof(InvalidOperationException));
    }

    [Fact]
    public void Constructor_PurgesLogsOlderThanRetention_KeepsRecent()
    {
        Directory.CreateDirectory(_dir);
        var today = new DateTimeOffset(2026, 6, 5, 10, 0, 0, TimeSpan.Zero);

        // Seed files at known ages relative to "today". Retention = 30 days.
        var old1 = Path.Combine(_dir, "zyncmaster-2026-04-01.log");   // 65 days old -> delete
        var old2 = Path.Combine(_dir, "zyncmaster-2026-05-05.log");   // 31 days old -> delete
        var keep1 = Path.Combine(_dir, "zyncmaster-2026-05-07.log");  // 29 days old -> keep
        var keep2 = Path.Combine(_dir, "zyncmaster-2026-06-05.log");  // today -> keep
        var unrelated = Path.Combine(_dir, "notes.txt");             // not ours -> keep
        File.WriteAllText(old1, "x");
        File.WriteAllText(old2, "x");
        File.WriteAllText(keep1, "x");
        File.WriteAllText(keep2, "x");
        File.WriteAllText(unrelated, "x");

        _ = new DailyFileLogger(_dir, LogLevel.Debug, FixedClock(today), retentionDays: 30);

        File.Exists(old1).Should().BeFalse();
        File.Exists(old2).Should().BeFalse();
        File.Exists(keep1).Should().BeTrue();
        File.Exists(keep2).Should().BeTrue();
        File.Exists(unrelated).Should().BeTrue("retention must only touch this logger's own files");
    }

    [Fact]
    public void Constructor_WithRetentionDisabled_KeepsAllLogs()
    {
        Directory.CreateDirectory(_dir);
        var today = new DateTimeOffset(2026, 6, 5, 10, 0, 0, TimeSpan.Zero);
        var ancient = Path.Combine(_dir, "zyncmaster-2020-01-01.log");
        File.WriteAllText(ancient, "x");

        _ = new DailyFileLogger(_dir, LogLevel.Debug, FixedClock(today), retentionDays: 0);

        File.Exists(ancient).Should().BeTrue();
    }

    [Fact]
    public async Task Log_IsThreadSafe_NoLostOrCorruptLines()
    {
        var when = new DateTimeOffset(2026, 6, 5, 10, 0, 0, TimeSpan.Zero);
        var sut = new DailyFileLogger(_dir, LogLevel.Debug, FixedClock(when));

        const int threads = 8;
        const int perThread = 200;
        var tasks = new List<Task>();
        for (var t = 0; t < threads; t++)
        {
            var id = t;
            tasks.Add(Task.Run(() =>
            {
                for (var i = 0; i < perThread; i++)
                    sut.Log(LogLevel.Info, $"t{id}-line{i}");
            }));
        }

        await Task.WhenAll(tasks);

        var lines = File.ReadAllLines(sut.CurrentLogFilePath());
        lines.Should().HaveCount(threads * perThread);
        // Every line must be a complete, well-formed entry (no interleaving/torn writes).
        lines.Should().OnlyContain(l => l.Contains("[Info]") && l.Contains("-line"));
    }
}
