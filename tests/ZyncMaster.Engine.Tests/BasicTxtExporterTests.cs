using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ZyncMaster.Engine;
using Xunit;

namespace ZyncMaster.Engine.Tests;

public sealed class BasicTxtExporterTests
{
    private sealed class FakeRunner : ICalExportRunner
    {
        public int SimpleCallCount;
        public int MonthCallCount;
        public int Year;
        public int Month;
        public IReadOnlyList<string>? Calendars;
        public bool IncludeCancelled;
        public string? OutputFilePath;

        public Task<string> ExportMonthAsync(int year, int month, IReadOnlyList<string>? calendarNames, CancellationToken ct)
        {
            MonthCallCount++;
            return Task.FromResult("{}");
        }

        public Task ExportSimpleAsync(int year, int month, IReadOnlyList<string>? calendarNames, bool includeCancelled, string outputFilePath, CancellationToken ct)
        {
            SimpleCallCount++;
            Year = year;
            Month = month;
            Calendars = calendarNames;
            IncludeCancelled = includeCancelled;
            OutputFilePath = outputFilePath;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListCalendarsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(new List<string>());
    }

    [Fact]
    public void Ctor_NullRunner_Throws()
    {
        Action act = () => new BasicTxtExporter(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task ExportAsync_NullOutputPath_Throws()
    {
        var exporter = new BasicTxtExporter(new FakeRunner());
        Func<Task> act = () => exporter.ExportAsync(2025, 5, null, includeCancelled: true, null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ExportAsync_DrivesRunnerInSimpleModeWithMonthAndOutputPath()
    {
        var runner = new FakeRunner();
        var exporter = new BasicTxtExporter(runner);
        var calendars = new[] { "Work" };

        await exporter.ExportAsync(2025, 5, calendars, includeCancelled: true, @"C:\out\may.txt", CancellationToken.None);

        runner.SimpleCallCount.Should().Be(1);
        runner.MonthCallCount.Should().Be(0);
        runner.Year.Should().Be(2025);
        runner.Month.Should().Be(5);
        runner.Calendars.Should().BeEquivalentTo(calendars);
        runner.IncludeCancelled.Should().BeTrue();
        runner.OutputFilePath.Should().Be(@"C:\out\may.txt");
    }

    [Fact]
    public async Task ExportAsync_PassesIncludeCancelledFlagThrough()
    {
        var runner = new FakeRunner();
        var exporter = new BasicTxtExporter(runner);

        await exporter.ExportAsync(2025, 5, null, includeCancelled: false, @"C:\out\may.txt", CancellationToken.None);

        runner.SimpleCallCount.Should().Be(1);
        runner.IncludeCancelled.Should().BeFalse();
    }

    [Fact]
    public async Task ExportAsync_NullCalendars_PassesNullThrough()
    {
        var runner = new FakeRunner();
        var exporter = new BasicTxtExporter(runner);

        await exporter.ExportAsync(2024, 12, null, includeCancelled: true, @"C:\out\dec.txt", CancellationToken.None);

        runner.SimpleCallCount.Should().Be(1);
        runner.Calendars.Should().BeNull();
    }
}
