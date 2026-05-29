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

public sealed class CalendarMirrorTests
{
    private static readonly DateTimeOffset From = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To   = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private static CalendarMirror MakeSut(FakeCalendarTarget target)
        => new CalendarMirror(
            target,
            new ImportPlanBuilder(),
            new EventDraftBuilder(new ParticipantBodyRenderer()));

    private static AppointmentRecord Rec(string id, bool cancelled = false)
        => new AppointmentRecord
        {
            Id          = id,
            Subject     = "S-" + id,
            IsCancelled = cancelled,
            StartOffset = new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero),
            EndOffset   = new DateTimeOffset(2026, 5, 10, 10, 0, 0, TimeSpan.Zero),
            StartTimeZoneId = "UTC",
        };

    // -- Hand-written target that records calls and lets tests script return values / throws.

    private sealed class FakeCalendarTarget : ICalendarTarget
    {
        public Dictionary<string, ExistingEventLookup> Existing { get; } =
            new Dictionary<string, ExistingEventLookup>(StringComparer.Ordinal);

        public List<ManagedEventRef> Managed { get; } = new List<ManagedEventRef>();

        public List<EventDraft> Created  { get; } = new List<EventDraft>();
        public List<(string EventId, EventDraft Draft)> Updated { get; } = new List<(string, EventDraft)>();
        public List<string> Deleted { get; } = new List<string>();

        // Optional hook to force a throw on create. Return true to throw.
        public Func<EventDraft, bool>? ThrowOnCreate;

        public Task<IReadOnlyList<CalendarTargetInfo>> ListCalendarsAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<CalendarTargetInfo> CreateCalendarAsync(string name, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyDictionary<string, ExistingEventLookup>> FindByExternalIdsAsync(
            string calendarId, IReadOnlyList<string> externalIds, CancellationToken ct = default)
        {
            IReadOnlyDictionary<string, ExistingEventLookup> map =
                externalIds
                    .Where(Existing.ContainsKey)
                    .ToDictionary(id => id, id => Existing[id], StringComparer.Ordinal);
            return Task.FromResult(map);
        }

        public Task<string> CreateEventAsync(string calendarId, EventDraft draft, CancellationToken ct = default)
        {
            if (ThrowOnCreate != null && ThrowOnCreate(draft))
                throw new InvalidOperationException("create boom: " + draft.ExternalId);
            Created.Add(draft);
            return Task.FromResult("new-" + draft.ExternalId);
        }

        public Task UpdateEventAsync(string eventId, EventDraft draft, CancellationToken ct = default)
        {
            Updated.Add((eventId, draft));
            return Task.CompletedTask;
        }

        public Task DeleteEventAsync(string eventId, CancellationToken ct = default)
        {
            Deleted.Add(eventId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ManagedEventRef>> ListManagedInWindowAsync(
            string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<ManagedEventRef>)Managed);
    }

    [Fact]
    public async Task Constructor_NullTarget_Throws()
    {
        Action act = () => new CalendarMirror(null!, new ImportPlanBuilder(), new EventDraftBuilder(new ParticipantBodyRenderer()));
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Constructor_NullPlanBuilder_Throws()
    {
        Action act = () => new CalendarMirror(new FakeCalendarTarget(), null!, new EventDraftBuilder(new ParticipantBodyRenderer()));
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Constructor_NullDraftBuilder_Throws()
    {
        Action act = () => new CalendarMirror(new FakeCalendarTarget(), new ImportPlanBuilder(), null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task CreatesMissing_TwoNew_CreatedIsTwo()
    {
        var target = new FakeCalendarTarget();
        var sut    = MakeSut(target);

        var outcome = await sut.MirrorAsync("CAL", new[] { Rec("a"), Rec("b") }, 30, From, To);

        outcome.Created.Should().Be(2);
        outcome.Updated.Should().Be(0);
        outcome.Deleted.Should().Be(0);
        outcome.Skipped.Should().Be(0);
        outcome.HadFailures.Should().BeFalse();
        target.Created.Select(d => d.ExternalId).Should().BeEquivalentTo("a", "b");
    }

    [Fact]
    public async Task UpdatesExisting_PassesCorrectEventId()
    {
        var target = new FakeCalendarTarget();
        target.Existing["a"] = new ExistingEventLookup { Id = "graph-a", BodyHtml = "<p>keep</p>" };
        var sut = MakeSut(target);

        var outcome = await sut.MirrorAsync("CAL", new[] { Rec("a") }, 30, From, To);

        outcome.Updated.Should().Be(1);
        outcome.Created.Should().Be(0);
        target.Updated.Should().ContainSingle();
        target.Updated[0].EventId.Should().Be("graph-a");
        target.Updated[0].Draft.ExternalId.Should().Be("a");
    }

    [Fact]
    public async Task DeletesManagedAbsentFromPayload()
    {
        var target = new FakeCalendarTarget();
        target.Managed.Add(new ManagedEventRef { SourceId = "s1", EventId = "ev-s1" });
        target.Managed.Add(new ManagedEventRef { SourceId = "s2", EventId = "ev-s2" });
        var sut = MakeSut(target);

        // Payload covers s1 only → s2 is an orphan and must be window-deleted.
        var outcome = await sut.MirrorAsync("CAL", new[] { Rec("s1") }, 30, From, To);

        target.Deleted.Should().Contain("ev-s2");
        target.Deleted.Should().NotContain("ev-s1");
        outcome.Deleted.Should().Be(1);
    }

    [Fact]
    public async Task NeverDeletes_WhenAllManagedPresent()
    {
        var target = new FakeCalendarTarget();
        target.Managed.Add(new ManagedEventRef { SourceId = "s1", EventId = "ev-s1" });
        target.Managed.Add(new ManagedEventRef { SourceId = "s2", EventId = "ev-s2" });
        var sut = MakeSut(target);

        var outcome = await sut.MirrorAsync("CAL", new[] { Rec("s1"), Rec("s2") }, 30, From, To);

        target.Deleted.Should().BeEmpty();
        outcome.Deleted.Should().Be(0);
    }

    [Fact]
    public async Task CancelledNotExisting_IsSkipped()
    {
        var target = new FakeCalendarTarget();
        var sut    = MakeSut(target);

        var outcome = await sut.MirrorAsync("CAL", new[] { Rec("a", cancelled: true) }, 30, From, To);

        outcome.Skipped.Should().Be(1);
        outcome.Deleted.Should().Be(0);
        target.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task CancelledExisting_IsDeletedViaPlan()
    {
        var target = new FakeCalendarTarget();
        target.Existing["a"] = new ExistingEventLookup { Id = "graph-a", BodyHtml = "" };
        var sut = MakeSut(target);

        var outcome = await sut.MirrorAsync("CAL", new[] { Rec("a", cancelled: true) }, 30, From, To);

        outcome.Deleted.Should().Be(1);
        target.Deleted.Should().Contain("graph-a");
    }

    [Fact]
    public async Task RecordsFailure_AndContinues()
    {
        var target = new FakeCalendarTarget();
        // Throw on the create of "b"; "a" and "c" must still be created.
        target.ThrowOnCreate = draft => draft.ExternalId == "b";
        var sut = MakeSut(target);

        var outcome = await sut.MirrorAsync("CAL", new[] { Rec("a"), Rec("b"), Rec("c") }, 30, From, To);

        outcome.Created.Should().Be(2);
        target.Created.Select(d => d.ExternalId).Should().BeEquivalentTo("a", "c");
        outcome.Failures.Should().HaveCount(1);
        outcome.HadFailures.Should().BeTrue();
    }
}
