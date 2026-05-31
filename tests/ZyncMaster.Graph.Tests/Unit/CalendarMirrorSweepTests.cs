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

// Data-loss guard tests for the conditional window sweep (plan v2 §B-2): a transient
// failure mid-upsert must NOT trigger the destructive sweep, so preexisting managed
// events the payload happened to miss are preserved.
public sealed class CalendarMirrorSweepTests
{
    private static readonly DateTimeOffset From = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To   = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static CalendarMirror MakeSut(ScriptableTarget target)
        => new CalendarMirror(
            target,
            new ImportPlanBuilder(),
            new EventDraftBuilder(new ParticipantBodyRenderer()));

    private static AppointmentRecord Rec(string id)
        => new AppointmentRecord
        {
            Id              = id,
            Subject         = "S-" + id,
            StartOffset     = new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero),
            EndOffset       = new DateTimeOffset(2026, 5, 10, 10, 0, 0, TimeSpan.Zero),
            StartTimeZoneId = "UTC",
        };

    private sealed class ScriptableTarget : ICalendarTarget
    {
        public List<ManagedEventRef> Managed { get; } = new();
        public List<string> Deleted { get; } = new();
        public int ListManagedCalls { get; private set; }

        // When set, CreateEventAsync throws this for the matching external id.
        public Func<string, Exception?>? ThrowOnCreate;

        public Task<IReadOnlyList<CalendarTargetInfo>> ListCalendarsAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<CalendarTargetInfo> CreateCalendarAsync(string name, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyDictionary<string, ExistingEventLookup>> FindByExternalIdsAsync(
            string calendarId, IReadOnlyList<string> externalIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, ExistingEventLookup>>(
                new Dictionary<string, ExistingEventLookup>(StringComparer.Ordinal));

        public Task<string> CreateEventAsync(string calendarId, EventDraft draft, CancellationToken ct = default)
        {
            var ex = ThrowOnCreate?.Invoke(draft.ExternalId);
            if (ex != null) throw ex;
            return Task.FromResult("new-" + draft.ExternalId);
        }

        public Task UpdateEventAsync(string eventId, EventDraft draft, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeleteEventAsync(string eventId, CancellationToken ct = default)
        {
            Deleted.Add(eventId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ManagedEventRef>> ListManagedInWindowAsync(
            string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
        {
            ListManagedCalls++;
            return Task.FromResult((IReadOnlyList<ManagedEventRef>)Managed);
        }
    }

    [Fact]
    public async Task TransientFailure_skips_sweep_and_preserves_managed_events()
    {
        var target = new ScriptableTarget();
        // A preexisting managed event that the (partial) payload does not cover. Under the
        // old unconditional sweep this would be deleted as an "orphan" — the data-loss bug.
        target.Managed.Add(new ManagedEventRef { SourceId = "preexisting", EventId = "ev-preexisting" });

        // Simulate a 429 mid-upsert on "b". The applied payload is now incomplete.
        target.ThrowOnCreate = id => id == "b"
            ? new GraphRequestException("Graph transient error after 3 attempts: 429 Too Many Requests. URL=...")
            : null;

        var sut = MakeSut(target);

        var outcome = await sut.MirrorAsync("CAL", new[] { Rec("a"), Rec("b"), Rec("c") }, 30, From, To);

        // The sweep must NOT have run and nothing must have been deleted.
        target.ListManagedCalls.Should().Be(0, "the sweep must be skipped after a transient failure");
        target.Deleted.Should().BeEmpty("no managed event may be deleted on a partial run");

        // The non-destructive work still happened: a and c created.
        outcome.Created.Should().Be(2);
        outcome.Deleted.Should().Be(0);

        // Reported as partial + transient so the caller retries.
        outcome.Partial.Should().BeTrue();
        outcome.HasTransientFailure.Should().BeTrue();
        outcome.Failures.Should().ContainSingle()
            .Which.Kind.Should().Be(SyncErrorKind.Transient);
    }

    [Fact]
    public async Task NoTransientFailure_runs_sweep_normally()
    {
        var target = new ScriptableTarget();
        target.Managed.Add(new ManagedEventRef { SourceId = "orphan", EventId = "ev-orphan" });

        var sut = MakeSut(target);

        // Clean run: payload is complete, so the orphan is genuinely gone and IS swept.
        var outcome = await sut.MirrorAsync("CAL", new[] { Rec("a") }, 30, From, To);

        target.ListManagedCalls.Should().Be(1, "the sweep runs on a clean payload");
        target.Deleted.Should().ContainSingle().Which.Should().Be("ev-orphan");
        outcome.Partial.Should().BeFalse();
        outcome.HadFailures.Should().BeFalse();
        outcome.Deleted.Should().Be(1);
    }

    [Fact]
    public async Task FatalFailure_still_runs_sweep()
    {
        // A Fatal (non-transient) failure does NOT mean the payload is incomplete in the
        // dangerous way a 429 does — the item was reached and definitively rejected. The
        // sweep still runs; only transient failures block it.
        var target = new ScriptableTarget();
        target.Managed.Add(new ManagedEventRef { SourceId = "orphan", EventId = "ev-orphan" });
        target.ThrowOnCreate = id => id == "b" ? new InvalidOperationException("bad payload") : null;

        var sut = MakeSut(target);

        var outcome = await sut.MirrorAsync("CAL", new[] { Rec("a"), Rec("b") }, 30, From, To);

        target.ListManagedCalls.Should().Be(1);
        outcome.Partial.Should().BeFalse();
        outcome.HasTransientFailure.Should().BeFalse();
        outcome.Failures.Should().ContainSingle().Which.Kind.Should().Be(SyncErrorKind.Fatal);
    }

    [Fact]
    public async Task UserRecoverableFailure_still_runs_sweep()
    {
        var target = new ScriptableTarget();
        target.Managed.Add(new ManagedEventRef { SourceId = "orphan", EventId = "ev-orphan" });
        target.ThrowOnCreate = id => id == "b"
            ? new AuthenticationFailedException("token expired")
            : null;

        var sut = MakeSut(target);

        var outcome = await sut.MirrorAsync("CAL", new[] { Rec("a"), Rec("b") }, 30, From, To);

        target.ListManagedCalls.Should().Be(1);
        outcome.Partial.Should().BeFalse();
        outcome.Failures.Should().ContainSingle().Which.Kind.Should().Be(SyncErrorKind.UserRecoverable);
    }
}
