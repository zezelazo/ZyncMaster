using System;
using System.Collections.Generic;
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
        public MirrorResult Next { get; set; } = new();

        public Task<IReadOnlyList<CalendarOption>> ListCalendarsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CalendarOption>>(Array.Empty<CalendarOption>());

        public Task<MirrorResult> MirrorAsync(
            string calendarId, IReadOnlyList<AppointmentRecord> records, int reminderMinutes,
            DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
        {
            Calls.Add((calendarId, records, reminderMinutes, fromUtc, toUtc));
            return Task.FromResult(Next);
        }
    }

    private sealed class RecordingReader : ICalendarReader
    {
        public List<AppointmentRecord> Window { get; set; } = new();
        public Exception? Throw { get; set; }
        public List<(string calendarId, DateTimeOffset from, DateTimeOffset to)> Calls { get; } = new();

        public Task<IReadOnlyList<AppointmentRecord>> ReadWindowAsync(
            string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
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
}
