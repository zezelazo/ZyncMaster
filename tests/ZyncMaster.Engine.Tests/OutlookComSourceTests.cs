using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ZyncMaster.Core;
using ZyncMaster.Engine;
using Xunit;

namespace ZyncMaster.Engine.Tests;

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

        public Task ExportSimpleAsync(int year, int month, IReadOnlyList<string>? calendarNames, bool includeCancelled, string outputFilePath, CancellationToken ct)
            => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListCalendarsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(new List<string>());
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

        var result = await sut.ReadWindowAsync(from, to, null, CancellationToken.None);

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

        var result = await sut.ReadWindowAsync(from, to, null, CancellationToken.None);

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

        var result = await sut.ReadWindowAsync(from, to, null, CancellationToken.None);

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

        var result = await sut.ReadWindowAsync(from, to, null, CancellationToken.None);

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

        var result = await sut.ReadWindowAsync(from, to, null, CancellationToken.None);

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

        var result = await sut.ReadWindowAsync(from, to, null, CancellationToken.None);

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

        await sut.ReadWindowAsync(from, to, null, CancellationToken.None);

        runner.Calls.Should().NotBeEmpty();
        runner.Calls[0].Calendars.Should().BeEquivalentTo(calendars);
    }

    // Feature 2 — a per-pair selection passed to ReadWindowAsync overrides the constructor default.
    [Fact]
    public async Task ReadWindow_PerCallSelection_OverridesConstructorDefault()
    {
        var runner = new FakeCalExportRunner(new());
        var sut = BuildSut(runner, new[] { "DeviceDefault" });

        var from = new DateTimeOffset(2025, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var to   = new DateTimeOffset(2025, 5, 2, 0, 0, 0, TimeSpan.Zero);

        await sut.ReadWindowAsync(from, to, new[] { "Work", "Personal" }, CancellationToken.None);

        runner.Calls[0].Calendars.Should().BeEquivalentTo(new[] { "Work", "Personal" });
    }

    // null selection + null constructor default => "all calendars" (the runner receives null).
    [Fact]
    public async Task ReadWindow_NullSelectionAndNoDefault_ReadsAll()
    {
        var runner = new FakeCalExportRunner(new());
        var sut = BuildSut(runner, null);

        var from = new DateTimeOffset(2025, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var to   = new DateTimeOffset(2025, 5, 2, 0, 0, 0, TimeSpan.Zero);

        await sut.ReadWindowAsync(from, to, null, CancellationToken.None);

        runner.Calls[0].Calendars.Should().BeNull();
    }

    // Builds one Complete-mode all-day event spanning [start, end) days.
    private static string AllDayEvent(string id, string subject, string start, string end) => $@"{{
      ""id"": ""{id}"",
      ""subject"": ""{subject}"",
      ""isAllDay"": true,
      ""isCancelled"": false,
      ""start"": ""{start}"",
      ""startTimeZoneId"": ""UTC"",
      ""startTimeZoneDisplayName"": ""(UTC) UTC"",
      ""end"": ""{end}"",
      ""durationMinutes"": 1440,
      ""organizer"": {{ ""name"": ""A"", ""email"": ""a@x.com"" }},
      ""description"": """",
      ""participants"": []
    }}";

    // FIX 1 (data loss) — read membership must equal the destination sweep membership (OVERLAP),
    // not START-only. A multi-day event whose START is before `from` but that OVERLAPS the window
    // is enumerated by the destination's calendarView sweep, so it MUST also be in the read set;
    // otherwise the sweep deletes a still-live event every cycle.
    [Fact]
    public async Task ReadWindow_MultiDayEventStartingBeforeFrom_OverlappingWindow_IsIncluded()
    {
        // Starts 05-04 (before from=05-05), runs through 05-07 (ends inside the window).
        var spanning = AllDayEvent("span", "Spanning", "2025-05-04T00:00:00+00:00", "2025-05-07T00:00:00+00:00");

        var runner = new FakeCalExportRunner(new() { [(2025, 5)] = Month(2025, 5, spanning) });
        var sut = BuildSut(runner);

        var from = new DateTimeOffset(2025, 5, 5, 0, 0, 0, TimeSpan.Zero);
        var to   = new DateTimeOffset(2025, 5, 20, 0, 0, 0, TimeSpan.Zero);

        var result = await sut.ReadWindowAsync(from, to, null, CancellationToken.None);

        result.Select(e => e.Subject).Should().ContainSingle().Which.Should().Be("Spanning",
            "an event that overlaps the window must be read so the destination sweep keeps it");
    }

    // An event that ENDS exactly at `from` does NOT overlap [from, to] (calendarView treats the
    // lower bound as exclusive of an event ending at startDateTime), so it is excluded.
    [Fact]
    public async Task ReadWindow_EventEndingExactlyAtFrom_IsExcluded()
    {
        var endsAtFrom = Event("e", "EndsAtFrom", "2025-05-04T23:00:00+00:00", "2025-05-05T00:00:00+00:00");

        var runner = new FakeCalExportRunner(new() { [(2025, 5)] = Month(2025, 5, endsAtFrom) });
        var sut = BuildSut(runner);

        var from = new DateTimeOffset(2025, 5, 5, 0, 0, 0, TimeSpan.Zero);
        var to   = new DateTimeOffset(2025, 5, 20, 0, 0, 0, TimeSpan.Zero);

        var result = await sut.ReadWindowAsync(from, to, null, CancellationToken.None);

        result.Should().BeEmpty();
    }

    // An event that starts AFTER `to` does not overlap the window and is excluded.
    [Fact]
    public async Task ReadWindow_EventStartingAfterTo_IsExcluded()
    {
        var afterTo = Event("a", "AfterTo", "2025-05-21T12:00:00+00:00", "2025-05-21T13:00:00+00:00");

        var runner = new FakeCalExportRunner(new() { [(2025, 5)] = Month(2025, 5, afterTo) });
        var sut = BuildSut(runner);

        var from = new DateTimeOffset(2025, 5, 5, 0, 0, 0, TimeSpan.Zero);
        var to   = new DateTimeOffset(2025, 5, 20, 0, 0, 0, TimeSpan.Zero);

        var result = await sut.ReadWindowAsync(from, to, null, CancellationToken.None);

        result.Should().BeEmpty();
    }

    // An event that starts exactly at `to` overlaps the closed upper bound (StartOffset <= toUtc),
    // consistent with calendarView including an event starting at endDateTime.
    [Fact]
    public async Task ReadWindow_EventStartingExactlyAtTo_IsIncluded()
    {
        var onTo = Event("t", "OnTo", "2025-05-20T00:00:00+00:00", "2025-05-20T01:00:00+00:00");

        var runner = new FakeCalExportRunner(new() { [(2025, 5)] = Month(2025, 5, onTo) });
        var sut = BuildSut(runner);

        var from = new DateTimeOffset(2025, 5, 5, 0, 0, 0, TimeSpan.Zero);
        var to   = new DateTimeOffset(2025, 5, 20, 0, 0, 0, TimeSpan.Zero);

        var result = await sut.ReadWindowAsync(from, to, null, CancellationToken.None);

        result.Select(e => e.Subject).Should().ContainSingle().Which.Should().Be("OnTo");
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
