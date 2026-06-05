using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ZyncMaster.Core;
using ZyncMaster.Graph;
using Xunit;

namespace ZyncMaster.Graph.Tests;

// FIX 1 (e2e mirror) — a recurring series mirrored to a Graph destination must upsert as N
// distinct events, and a SECOND run over the SAME source set must UPDATE each one 1:1 (never
// N→1) while the sweep deletes NO live occurrence.
//
// The root-cause bug folded every occurrence of a series onto one AppointmentRecord.Id; here we
// already have distinct per-occurrence ids (the production reader now produces them), and prove
// the mirror behaves correctly with them: RUN1 = N creates, RUN2 = N updates + 0 deletes.
public sealed class CalendarMirrorRecurrenceTests
{
    private static readonly DateTimeOffset From = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To   = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    private static CalendarMirror MakeSut(StatefulTarget target)
        => new CalendarMirror(target, new ImportPlanBuilder(), new EventDraftBuilder(new ParticipantBodyRenderer()));

    // Three occurrences of one series: same subject, distinct per-occurrence ids (as the reader
    // now produces via OccurrenceId.For), distinct starts.
    private static IReadOnlyList<AppointmentRecord> Series() => new[]
    {
        Occ("occ-mon", new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero)),
        Occ("occ-tue", new DateTimeOffset(2026, 6, 2, 9, 0, 0, TimeSpan.Zero)),
        Occ("occ-wed", new DateTimeOffset(2026, 6, 3, 9, 0, 0, TimeSpan.Zero)),
    };

    private static AppointmentRecord Occ(string id, DateTimeOffset start) => new AppointmentRecord
    {
        Id              = id,
        Subject         = "Standup",
        StartOffset     = start,
        EndOffset       = start.AddMinutes(15),
        StartTimeZoneId = "UTC",
    };

    // A target that actually REMEMBERS what was created, keyed by external id, so FindByExternalIds
    // and ListManagedInWindow reflect prior runs — modelling the real Graph round-trip closely
    // enough to expose an N→1 collapse (it would surface as updates != createdCount).
    private sealed class StatefulTarget : ICalendarTarget
    {
        // externalId -> (eventId, bodyHtml)
        private readonly Dictionary<string, (string EventId, string Body)> _store = new(StringComparer.Ordinal);
        private int _seq;

        public int Creates { get; private set; }
        public int Updates { get; private set; }
        public List<string> DeletedEventIds { get; } = new();

        public Task<IReadOnlyList<CalendarTargetInfo>> ListCalendarsAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<CalendarTargetInfo> CreateCalendarAsync(string name, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyDictionary<string, ExistingEventLookup>> FindByExternalIdsAsync(
            string calendarId, IReadOnlyList<string> externalIds, CancellationToken ct = default)
        {
            var map = new Dictionary<string, ExistingEventLookup>(StringComparer.Ordinal);
            foreach (var id in externalIds)
                if (_store.TryGetValue(id, out var v))
                    map[id] = new ExistingEventLookup { Id = v.EventId, BodyHtml = v.Body };
            return Task.FromResult<IReadOnlyDictionary<string, ExistingEventLookup>>(map);
        }

        public Task<string> CreateEventAsync(string calendarId, EventDraft draft, CancellationToken ct = default)
        {
            Creates++;
            var eventId = "ev-" + (++_seq);
            _store[draft.ExternalId] = (eventId, draft.BodyHtml ?? "");
            return Task.FromResult(eventId);
        }

        public Task UpdateEventAsync(string eventId, EventDraft draft, CancellationToken ct = default)
        {
            Updates++;
            // Keep the store consistent: the external id still maps to this event id.
            _store[draft.ExternalId] = (eventId, draft.BodyHtml ?? "");
            return Task.CompletedTask;
        }

        public Task DeleteEventAsync(string eventId, CancellationToken ct = default)
        {
            DeletedEventIds.Add(eventId);
            var key = _store.FirstOrDefault(kv => kv.Value.EventId == eventId).Key;
            if (key != null) _store.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ManagedEventRef>> ListManagedInWindowAsync(
            string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
        {
            IReadOnlyList<ManagedEventRef> refs = _store
                .Select(kv => new ManagedEventRef { SourceId = kv.Key, EventId = kv.Value.EventId })
                .ToList();
            return Task.FromResult(refs);
        }
    }

    [Fact]
    public async Task Run1_creates_N_distinct_events_then_Run2_updates_each_1to1_and_sweeps_none()
    {
        var target = new StatefulTarget();
        var sut = MakeSut(target);
        var series = Series();

        // RUN 1 — three distinct occurrences -> three creates, no updates, no deletes.
        var run1 = await sut.MirrorAsync("CAL", series, 30, From, To);
        run1.Created.Should().Be(3);
        run1.Updated.Should().Be(0);
        run1.Deleted.Should().Be(0);
        target.Creates.Should().Be(3, "each occurrence must create its own destination event");

        // RUN 2 — identical source set. Each occurrence already exists, so it is an UPDATE 1:1,
        // never one event updated three times, and the sweep finds nothing orphaned.
        var run2 = await sut.MirrorAsync("CAL", series, 30, From, To);
        run2.Created.Should().Be(0);
        run2.Updated.Should().Be(3, "each live occurrence must update its OWN event (1:1, not N→1)");
        run2.Deleted.Should().Be(0, "no live occurrence may be swept");
        target.DeletedEventIds.Should().BeEmpty("the sweep must not delete any live occurrence");
    }

    [Fact]
    public async Task Sweep_deletes_only_the_occurrence_dropped_from_the_source()
    {
        var target = new StatefulTarget();
        var sut = MakeSut(target);

        // RUN 1 — full series of three.
        await sut.MirrorAsync("CAL", Series(), 30, From, To);
        target.Creates.Should().Be(3);

        // RUN 2 — the user deleted Tuesday's occurrence at the source. Only that one occurrence
        // must be swept; Monday and Wednesday survive.
        var remaining = Series().Where(r => r.Id != "occ-tue").ToList();
        var run2 = await sut.MirrorAsync("CAL", remaining, 30, From, To);

        run2.Updated.Should().Be(2);
        run2.Deleted.Should().Be(1, "exactly the dropped occurrence is swept");
        target.DeletedEventIds.Should().ContainSingle();
    }
}
