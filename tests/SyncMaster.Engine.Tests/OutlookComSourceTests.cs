using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SyncMaster.Core;
using SyncMaster.Engine;
using Xunit;

namespace SyncMaster.Engine.Tests;

public sealed class OutlookComSourceTests
{
    // Fake runner that returns canned Complete-mode JSON per (year, month) and
    // records how many times it was invoked and with which arguments.
    private sealed class FakeCalExportRunner : ICalExportRunner
    {
        private readonly Dictionary<(int, int), string> _byMonth;
        public List<(int Year, int Month, IReadOnlyList<string>? Calendars)> Calls { get; } = new();

        public FakeCalExportRunner(Dictionary<(int, int), string> byMonth) => _byMonth = byMonth;

        public Task<string> ExportMonthAsync(int year, int month, IReadOnlyList<string>? calendarNames, CancellationToken ct)
        {
            Calls.Add((year, month, calendarNames));
            return Task.FromResult(_byMonth.TryGetValue((year, month), out var json) ? json : EmptyMonth(year, month));
        }
    }

    private static string EmptyMonth(int year, int month) => $@"{{
      ""exportedAt"": ""2025-01-01T00:00:00Z"",
      ""period"": {{ ""year"": {year}, ""month"": {month}, ""monthName"": ""X"" }},
      ""calendars"": [""Work""],
      ""events"": []
    }}";

    // Builds one Complete-mode event. start is an ISO 8601 offset string.
    private static string Event(string id, string subject, string start, string end) => $@"{{
      ""id"": ""{id}"",
      ""subject"": ""{subject}"",
      ""isAllDay"": false,
      ""isCancelled"": false,
      ""start"": ""{start}"",
      ""startTimeZoneId"": ""UTC"",
      ""startTimeZoneDisplayName"": ""(UTC) UTC"",
      ""end"": ""{end}"",
      ""durationMinutes"": 60,
      ""organizer"": {{ ""name"": ""A"", ""email"": ""a@x.com"" }},
      ""description"": """",
      ""participants"": []
    }}";

    private static string Month(int year, int month, params string[] events) => $@"{{
      ""exportedAt"": ""2025-01-01T00:00:00Z"",
      ""period"": {{ ""year"": {year}, ""month"": {month}, ""monthName"": ""X"" }},
      ""calendars"": [""Work""],
      ""events"": [{string.Join(",", events)}]
    }}";

    private static OutlookComSource BuildSut(FakeCalExportRunner runner, IReadOnlyList<string>? calendars = null)
        => new OutlookComSource(runner, new CompleteCalendarReader(), calendars);

    [Fact]
    public async Task ReadWindow_WithinOneMonth_CallsRunnerOnce_FiltersToWindow()
    {
        // Use UTC offsets so local/UTC align regardless of test machine TZ.
        var inside = Event("a", "Inside", "2025-05-10T12:00:00+00:00", "2025-05-10T13:00:00+00:00");
        var before = Event("b", "Before", "2025-05-01T12:00:00+00:00", "2025-05-01T13:00:00+00:00");
        var after  = Event("c", "After",  "2025-05-28T12:00:00+00:00", "2025-05-28T13:00:00+00:00");

        var runner = new FakeCalExportRunner(new() { [(2025, 5)] = Month(2025, 5, inside, before, after) });
        var sut = BuildSut(runner);

        var from = new DateTimeOffset(2025, 5, 5, 0, 0, 0, TimeSpan.Zero);
        var to   = new DateTimeOffset(2025, 5, 20, 0, 0, 0, TimeSpan.Zero);

        var result = await sut.ReadWindowAsync(from, to, CancellationToken.None);

        runner.Calls.Should().HaveCount(1);
        runner.Calls[0].Year.Should().Be(2025);
        runner.Calls[0].Month.Should().Be(5);
        result.Select(e => e.Subject).Should().ContainSingle().Which.Should().Be("Inside");
    }

    [Fact]
    public async Task ReadWindow_SpanningTwoMonths_CallsRunnerForBoth_MergesFilters()
    {
        var aprilEv = Event("apr", "AprilInside", "2025-04-29T12:00:00+00:00", "2025-04-29T13:00:00+00:00");
        var mayEv   = Event("may", "MayInside",   "2025-05-02T12:00:00+00:00", "2025-05-02T13:00:00+00:00");
        var mayLate = Event("late", "MayLate",    "2025-05-20T12:00:00+00:00", "2025-05-20T13:00:00+00:00");

        var runner = new FakeCalExportRunner(new()
        {
            [(2025, 4)] = Month(2025, 4, aprilEv),
            [(2025, 5)] = Month(2025, 5, mayEv, mayLate),
        });
        var sut = BuildSut(runner);

        var from = new DateTimeOffset(2025, 4, 28, 0, 0, 0, TimeSpan.Zero);
        var to   = new DateTimeOffset(2025, 5, 5, 0, 0, 0, TimeSpan.Zero);

        var result = await sut.ReadWindowAsync(from, to, CancellationToken.None);

        runner.Calls.Select(c => (c.Year, c.Month)).Should().BeEquivalentTo(new[] { (2025, 4), (2025, 5) });
        result.Select(e => e.Subject).Should().BeEquivalentTo(new[] { "AprilInside", "MayInside" });
    }

    [Fact]
    public async Task ReadWindow_DuplicateIdAcrossMonths_KeepsFirst()
    {
        // Same source id present in both months (same start -> same per-occurrence id).
        var dupApr = Event("shared", "FromApril", "2025-04-30T12:00:00+00:00", "2025-04-30T13:00:00+00:00");
        var dupMay = Event("shared", "FromMay",   "2025-04-30T12:00:00+00:00", "2025-04-30T13:00:00+00:00");

        var runner = new FakeCalExportRunner(new()
        {
            [(2025, 4)] = Month(2025, 4, dupApr),
            [(2025, 5)] = Month(2025, 5, dupMay),
        });
        var sut = BuildSut(runner);

        var from = new DateTimeOffset(2025, 4, 1, 0, 0, 0, TimeSpan.Zero);
        var to   = new DateTimeOffset(2025, 5, 31, 0, 0, 0, TimeSpan.Zero);

        var result = await sut.ReadWindowAsync(from, to, CancellationToken.None);

        result.Should().ContainSingle();
        result[0].Subject.Should().Be("FromApril");
    }

    [Fact]
    public async Task ReadWindow_EventExactlyOnFrom_Included_AfterTo_Excluded()
    {
        var onFrom = Event("f", "OnFrom", "2025-05-05T00:00:00+00:00", "2025-05-05T01:00:00+00:00");
        var afterTo = Event("t", "AfterTo", "2025-05-20T00:00:01+00:00", "2025-05-20T01:00:00+00:00");

        var runner = new FakeCalExportRunner(new() { [(2025, 5)] = Month(2025, 5, onFrom, afterTo) });
        var sut = BuildSut(runner);

        var from = new DateTimeOffset(2025, 5, 5, 0, 0, 0, TimeSpan.Zero);
        var to   = new DateTimeOffset(2025, 5, 20, 0, 0, 0, TimeSpan.Zero);

        var result = await sut.ReadWindowAsync(from, to, CancellationToken.None);

        result.Select(e => e.Subject).Should().ContainSingle().Which.Should().Be("OnFrom");
    }

    [Fact]
    public async Task ReadWindow_AllOutsideWindow_ReturnsEmpty()
    {
        var before = Event("b", "Before", "2025-05-01T12:00:00+00:00", "2025-05-01T13:00:00+00:00");
        var after  = Event("c", "After",  "2025-05-28T12:00:00+00:00", "2025-05-28T13:00:00+00:00");

        var runner = new FakeCalExportRunner(new() { [(2025, 5)] = Month(2025, 5, before, after) });
        var sut = BuildSut(runner);

        var from = new DateTimeOffset(2025, 5, 10, 0, 0, 0, TimeSpan.Zero);
        var to   = new DateTimeOffset(2025, 5, 20, 0, 0, 0, TimeSpan.Zero);

        var result = await sut.ReadWindowAsync(from, to, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadWindow_OrdersByStartOffset()
    {
        var late  = Event("l", "Late",  "2025-05-18T12:00:00+00:00", "2025-05-18T13:00:00+00:00");
        var early = Event("e", "Early", "2025-05-08T12:00:00+00:00", "2025-05-08T13:00:00+00:00");

        var runner = new FakeCalExportRunner(new() { [(2025, 5)] = Month(2025, 5, late, early) });
        var sut = BuildSut(runner);

        var from = new DateTimeOffset(2025, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var to   = new DateTimeOffset(2025, 5, 31, 0, 0, 0, TimeSpan.Zero);

        var result = await sut.ReadWindowAsync(from, to, CancellationToken.None);

        result.Select(e => e.Subject).Should().ContainInOrder("Early", "Late");
    }

    [Fact]
    public async Task ReadWindow_PassesCalendarNamesToRunner()
    {
        var runner = new FakeCalExportRunner(new());
        var calendars = new[] { "Work", "Personal" };
        var sut = BuildSut(runner, calendars);

        var from = new DateTimeOffset(2025, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var to   = new DateTimeOffset(2025, 5, 2, 0, 0, 0, TimeSpan.Zero);

        await sut.ReadWindowAsync(from, to, CancellationToken.None);

        runner.Calls.Should().NotBeEmpty();
        runner.Calls[0].Calendars.Should().BeEquivalentTo(calendars);
    }

    [Fact]
    public void Ctor_NullRunner_Throws()
    {
        Action act = () => new OutlookComSource(null!, new CompleteCalendarReader(), null);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullReader_Throws()
    {
        Action act = () => new OutlookComSource(new FakeCalExportRunner(new()), null!, null);
        act.Should().Throw<ArgumentNullException>();
    }
}
