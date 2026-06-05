using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ZyncMaster.Core;
using ZyncMaster.Graph;
using ZyncMaster.Server;
using Xunit;

namespace ZyncMaster.Server.Tests.Sync;

// CalendarSyncModule owns the read+mirror that previously lived inline in PairEndpoints' /run:
// resolve the source reader and destination writer (via ProviderRegistry), read the window
// guarded against a transient failure, then call the writer's MirrorAsync (which is the
// CalendarMirror entry point). The run-lock and the pair lookup stay in the endpoint.
public sealed class CalendarSyncModuleTests
{
    private sealed class RecordingWriter : ICalendarWriter
    {
        public List<(string calendarId, IReadOnlyList<AppointmentRecord> records, int reminder, DateTimeOffset from, DateTimeOffset to)> Calls { get; } = new();
        public List<string> PairIds { get; } = new();
        public MirrorResult Next { get; set; } = new();

        public Task<IReadOnlyList<CalendarOption>> ListCalendarsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CalendarOption>>(Array.Empty<CalendarOption>());

        public Task<CalendarOption> CreateCalendarAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(new CalendarOption { Id = "new", DisplayName = name });

        public Task<MirrorResult> MirrorAsync(
            string calendarId, IReadOnlyList<AppointmentRecord> records, int reminderMinutes,
            DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default, string pairId = "")
        {
            Calls.Add((calendarId, records, reminderMinutes, fromUtc, toUtc));
            PairIds.Add(pairId);
            return Task.FromResult(Next);
        }
    }

    private sealed class RecordingReader : ICalendarReader
    {
        public List<AppointmentRecord> Window { get; set; } = new();
        public Exception? Throw { get; set; }
        public List<(string calendarId, DateTimeOffset from, DateTimeOffset to)> Calls { get; } = new();

        public Task<IReadOnlyList<CalendarOption>> ListCalendarsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CalendarOption>>(Array.Empty<CalendarOption>());

        public Task<IReadOnlyList<AppointmentRecord>> ReadWindowAsync(
            string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc,
            CancellationToken ct = default, bool preserveLocalTime = false)
        {
            Calls.Add((calendarId, fromUtc, toUtc));
            if (Throw is not null)
                throw Throw;
            return Task.FromResult<IReadOnlyList<AppointmentRecord>>(Window);
        }
    }

    private static SyncPair Pair(string sourceProvider) => new()
    {
        Id = "p1",
        Name = "Pair",
        Source = new Endpoint { Provider = sourceProvider, AccountRef = "default", CalendarId = "src-cal" },
        Destination = new Endpoint { Provider = "MicrosoftGraph", AccountRef = "default", CalendarId = "dst-cal" },
        IntervalMin = 15,
    };

    private static AppointmentRecord MakeEvent(string id) => new()
    {
        Id = id,
        Subject = id,
        StartOffset = DateTimeOffset.UtcNow.AddDays(1),
        EndOffset = DateTimeOffset.UtcNow.AddDays(1).AddHours(1),
        StartTimeZoneId = "UTC",
    };

    private static (CalendarSyncModule module, RecordingReader reader, RecordingWriter writer) BuildModule(
        RecordingReader? reader, RecordingWriter writer)
    {
        var r = reader ?? new RecordingReader();
        var registry = new ProviderRegistry(
            readerFactory: _ => r,
            writerFactory: _ => writer);
        return (new CalendarSyncModule(registry), r, writer);
    }

    [Fact]
    public async Task ExecuteAsync_reads_source_and_mirrors_to_destination()
    {
        var reader = new RecordingReader { Window = new() { MakeEvent("x"), MakeEvent("y"), MakeEvent("z") } };
        var writer = new RecordingWriter { Next = new MirrorResult { Created = 3 } };
        var (module, _, _) = BuildModule(reader, writer);

        var from = DateTimeOffset.UtcNow;
        var to = from.AddDays(30);

        var outcome = await module.ExecuteAsync(Pair("MicrosoftGraph"), from, to);

        outcome.NoServerReader.Should().BeFalse();
        outcome.Result!.Created.Should().Be(3);

        // The reader was asked for the source calendar over the window.
        reader.Calls.Should().ContainSingle().Which.calendarId.Should().Be("src-cal");
        reader.Calls[0].from.Should().Be(from);
        reader.Calls[0].to.Should().Be(to);

        // The module forwarded the read result to the writer (the CalendarMirror entry point)
        // for the destination calendar, with the 30-minute reminder and the same window.
        writer.Calls.Should().ContainSingle();
        writer.Calls[0].calendarId.Should().Be("dst-cal");
        writer.Calls[0].records.Should().HaveCount(3);
        writer.Calls[0].reminder.Should().Be(30);
        writer.Calls[0].from.Should().Be(from);
        writer.Calls[0].to.Should().Be(to);
        // The pair id is forwarded so the writer stamps each created event with CalImportPairId,
        // making a later destination cleanup able to target exactly this pair's events.
        writer.PairIds.Should().ContainSingle().Which.Should().Be("p1");
    }

    [Fact]
    public async Task ExecuteAsync_returns_no_server_reader_when_source_has_no_reader()
    {
        // ProviderRegistry returns a null reader for OutlookCom (it short-circuits the reader
        // factory for non-Graph providers) — exactly the inline /run no_server_reader path.
        var writer = new RecordingWriter();
        var registry = new ProviderRegistry(
            readerFactory: _ => null!, // never invoked for OutlookCom (registry short-circuits)
            writerFactory: _ => writer);
        var module = new CalendarSyncModule(registry);

        var outcome = await module.ExecuteAsync(Pair("OutlookCom"), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30));

        outcome.NoServerReader.Should().BeTrue();
        outcome.Result.Should().BeNull();
        writer.Calls.Should().BeEmpty("no read means no mirror");
    }

    [Fact]
    public async Task ExecuteAsync_transient_read_failure_is_partial_and_skips_the_mirror()
    {
        // A transient read failure must abort BEFORE the destructive mirror: the writer (and
        // therefore the CalendarMirror window sweep) is never called, and the result is Partial.
        var reader = new RecordingReader
        {
            Throw = new GraphRequestException("Graph transient error after 3 attempts: 503. URL=...", isTransient: true),
        };
        var writer = new RecordingWriter();
        var (module, _, _) = BuildModule(reader, writer);

        var outcome = await module.ExecuteAsync(Pair("MicrosoftGraph"), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30));

        outcome.NoServerReader.Should().BeFalse();
        outcome.Result!.Partial.Should().BeTrue();
        outcome.Result.Created.Should().Be(0);
        outcome.Result.Updated.Should().Be(0);
        outcome.Result.Deleted.Should().Be(0);
        outcome.Result.Failures.Should().ContainSingle().Which.Should().Contain("transient");

        writer.Calls.Should().BeEmpty("a transient read must never feed a short set into the destructive sweep");
    }

    [Fact]
    public async Task ExecuteAsync_non_transient_read_failure_propagates()
    {
        // Auth/consent and other non-transient errors must NOT be swallowed as Partial; they
        // propagate so the caller is told to reconnect rather than silently retried forever.
        var reader = new RecordingReader { Throw = new AuthenticationFailedException("token expired") };
        var writer = new RecordingWriter();
        var (module, _, _) = BuildModule(reader, writer);

        Func<Task> act = () => module.ExecuteAsync(Pair("MicrosoftGraph"), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30));

        await act.Should().ThrowAsync<AuthenticationFailedException>();
        writer.Calls.Should().BeEmpty();
    }

    [Fact]
    public void ModuleId_is_calendar()
    {
        var (module, _, _) = BuildModule(null, new RecordingWriter());
        module.ModuleId.Should().Be("calendar");
    }

    // ---------------- Feature 2: multi-calendar source merge ----------------

    // A reader that returns a DIFFERENT window per calendarId, and can be told to throw on one
    // specific calendar to model a transient failure mid-merge.
    private sealed class PerCalendarReader : ICalendarReader
    {
        public Dictionary<string, List<AppointmentRecord>> ByCalendar { get; } = new();
        public string? ThrowOnCalendar { get; set; }
        public Exception? Throw { get; set; }
        public List<string> ReadCalendars { get; } = new();
        // Calendars this SOURCE account exposes for the AllCalendars enumeration. The module must
        // enumerate the ORIGIN here (this reader), never the destination writer.
        public List<CalendarOption> Calendars { get; set; } = new();
        public int ListCalendarsCallCount { get; private set; }

        public Task<IReadOnlyList<CalendarOption>> ListCalendarsAsync(CancellationToken ct = default)
        {
            ListCalendarsCallCount++;
            return Task.FromResult<IReadOnlyList<CalendarOption>>(Calendars);
        }

        public Task<IReadOnlyList<AppointmentRecord>> ReadWindowAsync(
            string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc,
            CancellationToken ct = default, bool preserveLocalTime = false)
        {
            ReadCalendars.Add(calendarId);
            if (Throw is not null && ThrowOnCalendar == calendarId)
                throw Throw;
            return Task.FromResult<IReadOnlyList<AppointmentRecord>>(
                ByCalendar.TryGetValue(calendarId, out var list) ? list : new List<AppointmentRecord>());
        }
    }

    // A writer whose ListCalendarsAsync returns a configured set (for AllCalendars enumeration) and
    // records the mirror calls, implementing the interface directly so the registry calls THIS
    // ListCalendarsAsync (a `new` on a subclass would not dispatch through the interface).
    private sealed class ListingWriter : ICalendarWriter
    {
        public List<CalendarOption> Calendars { get; set; } = new();
        public List<(string calendarId, IReadOnlyList<AppointmentRecord> records, int reminder, DateTimeOffset from, DateTimeOffset to)> Calls { get; } = new();
        public MirrorResult Next { get; set; } = new();
        public int ListCalendarsCount { get; private set; }

        public Task<IReadOnlyList<CalendarOption>> ListCalendarsAsync(CancellationToken ct = default)
        {
            ListCalendarsCount++;
            return Task.FromResult<IReadOnlyList<CalendarOption>>(Calendars);
        }

        public Task<CalendarOption> CreateCalendarAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(new CalendarOption { Id = "new", DisplayName = name });

        public Task<MirrorResult> MirrorAsync(
            string calendarId, IReadOnlyList<AppointmentRecord> records, int reminderMinutes,
            DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default, string pairId = "")
        {
            Calls.Add((calendarId, records, reminderMinutes, fromUtc, toUtc));
            return Task.FromResult(Next);
        }
    }

    private static SyncPair PairWithSource(Endpoint source) => new()
    {
        Id = "p1",
        Name = "Pair",
        Source = source,
        Destination = new Endpoint { Provider = "MicrosoftGraph", AccountRef = "default", CalendarId = "dst-cal" },
        IntervalMin = 15,
    };

    [Fact]
    public async Task ExecuteAsync_reads_and_merges_an_explicit_calendarIds_subset()
    {
        var reader = new PerCalendarReader();
        reader.ByCalendar["c1"] = new() { MakeEvent("a"), MakeEvent("b") };
        reader.ByCalendar["c2"] = new() { MakeEvent("c") };
        reader.ByCalendar["c3"] = new() { MakeEvent("z") }; // NOT selected — must not be read

        var writer = new RecordingWriter { Next = new MirrorResult { Created = 3 } };
        var registry = new ProviderRegistry(readerFactory: _ => reader, writerFactory: _ => writer);
        var module = new CalendarSyncModule(registry);

        var source = new Endpoint
        {
            Provider = "MicrosoftGraph", AccountRef = "default", CalendarId = "c1",
            CalendarIds = new[] { "c1", "c2" },
        };

        var outcome = await module.ExecuteAsync(PairWithSource(source), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(14));

        outcome.NoServerReader.Should().BeFalse();
        reader.ReadCalendars.Should().BeEquivalentTo(new[] { "c1", "c2" });
        // The merged set is the union of c1 + c2, one writer call to the single destination.
        writer.Calls.Should().ContainSingle();
        writer.Calls[0].calendarId.Should().Be("dst-cal");
        writer.Calls[0].records.Select(r => r.Id).Should().BeEquivalentTo(new[] { "a", "b", "c" });
    }

    [Fact]
    public async Task ExecuteAsync_dedupes_the_same_event_across_source_calendars()
    {
        var reader = new PerCalendarReader();
        // The SAME event id "dup" appears in both calendars: it must collapse to ONE destination event.
        reader.ByCalendar["c1"] = new() { MakeEvent("dup"), MakeEvent("a") };
        reader.ByCalendar["c2"] = new() { MakeEvent("dup"), MakeEvent("b") };

        var writer = new RecordingWriter();
        var registry = new ProviderRegistry(readerFactory: _ => reader, writerFactory: _ => writer);
        var module = new CalendarSyncModule(registry);

        var source = new Endpoint
        {
            Provider = "MicrosoftGraph", AccountRef = "default", CalendarId = "c1",
            CalendarIds = new[] { "c1", "c2" },
        };

        await module.ExecuteAsync(PairWithSource(source), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(14));

        writer.Calls.Should().ContainSingle();
        writer.Calls[0].records.Select(r => r.Id).Should().BeEquivalentTo(new[] { "dup", "a", "b" });
    }

    [Fact]
    public async Task ExecuteAsync_allCalendars_enumerates_then_reads_every_calendar()
    {
        var reader = new PerCalendarReader
        {
            // AllCalendars enumerates the SOURCE account via the READER, not the destination writer.
            Calendars = new()
            {
                new CalendarOption { Id = "c1", DisplayName = "One" },
                new CalendarOption { Id = "c2", DisplayName = "Two" },
            },
        };
        reader.ByCalendar["c1"] = new() { MakeEvent("a") };
        reader.ByCalendar["c2"] = new() { MakeEvent("b") };

        var writer = new ListingWriter();
        var registry = new ProviderRegistry(readerFactory: _ => reader, writerFactory: _ => writer);
        var module = new CalendarSyncModule(registry);

        var source = new Endpoint
        {
            Provider = "MicrosoftGraph", AccountRef = "default", CalendarId = "c1",
            AllCalendars = true,
        };

        await module.ExecuteAsync(PairWithSource(source), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(14));

        reader.ListCalendarsCallCount.Should().Be(1, "AllCalendars must enumerate the SOURCE reader");
        reader.ReadCalendars.Should().BeEquivalentTo(new[] { "c1", "c2" });
        writer.Calls.Should().ContainSingle();
        writer.Calls[0].records.Select(r => r.Id).Should().BeEquivalentTo(new[] { "a", "b" });
    }

    [Fact]
    public async Task ExecuteAsync_allCalendars_enumerates_the_SOURCE_account_not_the_destination()
    {
        // Regression: when source and destination are DISTINCT accounts, "All calendars" must
        // enumerate (and read) the ORIGIN account's calendars — never the destination's. The reader
        // is resolved against pair.Source.AccountRef ("src-account") and the writer against
        // pair.Destination.AccountRef ("dst-account"); enumerating via the writer would read the
        // wrong mailbox entirely.
        var sourceReader = new PerCalendarReader
        {
            Calendars = new()
            {
                new CalendarOption { Id = "src-c1", DisplayName = "Source One" },
                new CalendarOption { Id = "src-c2", DisplayName = "Source Two" },
            },
        };
        sourceReader.ByCalendar["src-c1"] = new() { MakeEvent("s1") };
        sourceReader.ByCalendar["src-c2"] = new() { MakeEvent("s2") };

        // The DESTINATION writer exposes a completely different set of calendars; if the module ever
        // enumerated the destination, the reader would be asked for "dst-c1"/"dst-c2" and the test
        // would fail. We assert ListCalendarsAsync on the writer is never called.
        var destWriter = new ListingWriter
        {
            Calendars = new()
            {
                new CalendarOption { Id = "dst-c1", DisplayName = "Dest One" },
                new CalendarOption { Id = "dst-c2", DisplayName = "Dest Two" },
            },
        };

        // Factories branch on accountRef so reader↔source and writer↔destination are wired to the
        // RIGHT account, modelling ResolveReader(pair.Source) / ResolveWriter(pair.Destination).
        var registry = new ProviderRegistry(
            readerFactory: accountRef =>
            {
                accountRef.Should().Be("src-account", "the reader must resolve against the SOURCE account");
                return sourceReader;
            },
            writerFactory: accountRef =>
            {
                accountRef.Should().Be("dst-account", "the writer must resolve against the DESTINATION account");
                return destWriter;
            });
        var module = new CalendarSyncModule(registry);

        var pair = new SyncPair
        {
            Id = "p1",
            Name = "Pair",
            Source = new Endpoint
            {
                Provider = "MicrosoftGraph", AccountRef = "src-account", CalendarId = "src-c1",
                AllCalendars = true,
            },
            Destination = new Endpoint
            {
                Provider = "MicrosoftGraph", AccountRef = "dst-account", CalendarId = "dst-cal",
            },
            IntervalMin = 15,
        };

        await module.ExecuteAsync(pair, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(14));

        // The ORIGIN account was enumerated and read; the destination's calendars never leaked in.
        sourceReader.ListCalendarsCallCount.Should().Be(1);
        sourceReader.ReadCalendars.Should().BeEquivalentTo(new[] { "src-c1", "src-c2" });
        destWriter.ListCalendarsCount.Should().Be(0, "the destination writer must NOT be enumerated for source calendars");
        // The destination still received the merged ORIGIN events on its single calendar.
        destWriter.Calls.Should().ContainSingle();
        destWriter.Calls[0].calendarId.Should().Be("dst-cal");
        destWriter.Calls[0].records.Select(r => r.Id).Should().BeEquivalentTo(new[] { "s1", "s2" });
    }

    [Fact]
    public async Task ExecuteAsync_transient_failure_on_one_calendar_aborts_before_mirror()
    {
        // §A-3 across N reads: a transient failure reading the SECOND source calendar must abort the
        // whole run as Partial — the merged (short) set must never reach the destructive mirror.
        var reader = new PerCalendarReader
        {
            ThrowOnCalendar = "c2",
            Throw = new GraphRequestException("Graph transient: 503", isTransient: true),
        };
        reader.ByCalendar["c1"] = new() { MakeEvent("a") };

        var writer = new RecordingWriter();
        var registry = new ProviderRegistry(readerFactory: _ => reader, writerFactory: _ => writer);
        var module = new CalendarSyncModule(registry);

        var source = new Endpoint
        {
            Provider = "MicrosoftGraph", AccountRef = "default", CalendarId = "c1",
            CalendarIds = new[] { "c1", "c2" },
        };

        var outcome = await module.ExecuteAsync(PairWithSource(source), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(14));

        outcome.Result!.Partial.Should().BeTrue();
        writer.Calls.Should().BeEmpty("a transient read mid-merge must not feed a short set into the sweep");
    }

    [Fact]
    public async Task ExecuteAsync_legacy_single_calendar_reads_only_the_calendarId()
    {
        // No AllCalendars and no CalendarIds → legacy behaviour: read exactly Source.CalendarId.
        var reader = new PerCalendarReader();
        reader.ByCalendar["src-cal"] = new() { MakeEvent("a"), MakeEvent("b") };

        var writer = new RecordingWriter();
        var registry = new ProviderRegistry(readerFactory: _ => reader, writerFactory: _ => writer);
        var module = new CalendarSyncModule(registry);

        await module.ExecuteAsync(Pair("MicrosoftGraph"), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(14));

        reader.ReadCalendars.Should().BeEquivalentTo(new[] { "src-cal" });
        writer.Calls.Should().ContainSingle();
    }
}
