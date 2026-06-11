using System;
using System.Linq;
using FluentAssertions;
using ZyncMaster.Graph;
using Xunit;

namespace ZyncMaster.Graph.Tests;

// PRIVACY CONTRACT TESTS — spec 2026-06-10-calendar-v2-design §12. These are the loudest tests
// of calendar v2: a replica shares ONLY start/end (same duration), showAs and the user's manual
// mask title with the destination tenant. If any of these fails, job1 can see job2's data.
public sealed class ReplicaDraftBuilderTests
{
    private static SourceEventSnapshot Source() => new()
    {
        GraphEventId = "graph-ev-1",
        StableId = "7d3f9c2b-aaaa-bbbb-cccc-111122223333",
        Subject = "Secret board meeting about the merger",
        Start = new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero),
        End = new DateTimeOffset(2026, 6, 15, 11, 0, 0, TimeSpan.Zero),
        TimeZoneId = "UTC",
        IsAllDay = false,
        ShowAs = "tentative",
        IsCancelled = false,
        IsOrganizer = true,
        HasAttendees = true,
    };

    private readonly ReplicaDraftBuilder _sut = new();

    // §12 invariant 1 — the original title.
    [Fact]
    public void Replica_title_is_always_the_manual_mask_never_the_source_subject()
    {
        var draft = _sut.Build(Source(), "Busy");

        draft.MaskTitle.Should().Be("Busy");
        draft.MaskTitle.Should().NotContain("Secret",
            "no code path may copy the source subject into a replica");
    }

    [Fact]
    public void Builder_throws_when_mask_title_is_missing_so_no_code_path_can_default_to_the_source_subject()
    {
        var act = () => _sut.Build(Source(), "  ");

        act.Should().Throw<ArgumentException>(
            "an empty mask must never silently fall back to the original subject");
    }

    // §12 invariants 2/3/4/6/7 — by CONSTRUCTION: the ReplicaDraft type simply cannot carry
    // body, participants, location, organizer, categories, sensitivity, attachments or any
    // reference to sibling replicas. This reflection whitelist is the contract: adding ANY
    // property to ReplicaDraft makes this test fail and forces a privacy review.
    [Fact]
    public void ReplicaDraft_property_whitelist_is_exact()
    {
        var names = typeof(ReplicaDraft).GetProperties().Select(p => p.Name).OrderBy(n => n);

        names.Should().BeEquivalentTo(new[]
        {
            "End", "IsAllDay", "MaskTitle", "ShowAs", "SourceEventId", "Start", "TimeZoneId",
        }, "the replica draft is a WHITELIST — any extra property is a potential privacy leak");
    }

    [Fact]
    public void ReplicaDraft_type_cannot_carry_a_body_or_description()
    {
        typeof(ReplicaDraft).GetProperties()
            .Should().NotContain(p => p.Name.Contains("Body") || p.Name.Contains("Description"));
    }

    [Fact]
    public void ReplicaDraft_type_cannot_carry_participants_or_attendees()
    {
        typeof(ReplicaDraft).GetProperties()
            .Should().NotContain(p => p.Name.Contains("Participant") || p.Name.Contains("Attendee"));
    }

    [Fact]
    public void ReplicaDraft_type_cannot_carry_a_location()
    {
        typeof(ReplicaDraft).GetProperties()
            .Should().NotContain(p => p.Name.Contains("Location"));
    }

    // §12 invariant 5 — the source identity is an opaque UUID, never an email/account name.
    [Fact]
    public void Replica_source_marker_is_the_opaque_stable_id()
    {
        var draft = _sut.Build(Source(), "Busy");

        draft.SourceEventId.Should().Be("7d3f9c2b-aaaa-bbbb-cccc-111122223333");
        draft.SourceEventId.Should().NotContain("@");
    }

    // Hard rule §3 — same duration always; showAs travels; unknown showAs degrades to busy.
    [Fact]
    public void Replica_keeps_start_end_timezone_allday_and_showAs()
    {
        var draft = _sut.Build(Source(), "Busy");

        draft.Start.Should().Be(new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero));
        draft.End.Should().Be(new DateTimeOffset(2026, 6, 15, 11, 0, 0, TimeSpan.Zero));
        draft.TimeZoneId.Should().Be("UTC");
        draft.IsAllDay.Should().BeFalse();
        draft.ShowAs.Should().Be("tentative");
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("garbage")]
    public void Replica_degrades_unknown_showAs_to_busy(string showAs)
    {
        var draft = _sut.Build(Source() with { ShowAs = showAs }, "Busy");

        draft.ShowAs.Should().Be("busy");
    }

    [Fact]
    public void Build_throws_on_null_source()
    {
        var act = () => _sut.Build(null!, "Busy");
        act.Should().Throw<ArgumentNullException>();
    }
}
