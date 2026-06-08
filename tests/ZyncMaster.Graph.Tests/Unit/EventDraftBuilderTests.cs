using System;
using System.Collections.Generic;
using FluentAssertions;
using Moq;
using ZyncMaster.Graph;
using ZyncMaster.Core;
using Xunit;

namespace ZyncMaster.Graph.Tests;

public sealed class EventDraftBuilderTests
{
    private readonly EventDraftBuilder _sut = new EventDraftBuilder(new ParticipantBodyRenderer());

    private static AppointmentRecord MakeRecord(
        string id           = "id-1",
        bool   isAllDay     = false,
        string tz           = "Eastern Standard Time",
        bool   isCancelled  = false,
        string description  = "Desc",
        IReadOnlyList<ParticipantRecord>? participants = null)
    {
        var start = new DateTimeOffset(2025, 5, 15, 10, 0, 0, TimeSpan.FromHours(-5));
        var end   = start.AddHours(1);
        return new AppointmentRecord
        {
            Id              = id,
            Subject         = "Subject",
            IsAllDay        = isAllDay,
            IsCancelled     = isCancelled,
            StartOffset     = start,
            EndOffset       = end,
            StartTimeZoneId = tz,
            Description     = description,
            Participants    = participants ?? Array.Empty<ParticipantRecord>(),
        };
    }

    [Fact]
    public void BuildForCreate_MapsCoreFields()
    {
        var rec = MakeRecord();
        var d   = _sut.BuildForCreate(rec, reminderMinutes: 30);

        d.Subject.Should().Be("Subject");
        d.Start.Should().Be(rec.StartOffset);
        d.End.Should().Be(rec.EndOffset);
        d.TimeZoneId.Should().Be("Eastern Standard Time");
        d.IsAllDay.Should().BeFalse();
        d.ReminderMinutesBeforeStart.Should().Be(30);
        d.ExternalId.Should().Be("id-1");
        d.BodyHtml.Should().Contain("Desc");
    }

    [Fact]
    public void BuildForCreate_carries_pairId_when_supplied_and_empty_otherwise()
    {
        var rec = MakeRecord();

        _sut.BuildForCreate(rec, 30, pairId: "pair-7").PairId.Should().Be("pair-7");
        _sut.BuildForCreate(rec, 30).PairId.Should().Be("");
    }

    [Fact]
    public void BuildForUpdate_carries_pairId_when_supplied()
    {
        var rec = MakeRecord();
        _sut.BuildForUpdate(rec, 30, existingBodyHtml: "<p>x</p>", pairId: "pair-9").PairId.Should().Be("pair-9");
    }

    [Fact]
    public void BuildForCreate_EmptyTimeZone_FallsBackToUtc()
    {
        var rec = MakeRecord(tz: "");
        var d   = _sut.BuildForCreate(rec, 30);
        d.TimeZoneId.Should().Be("UTC");
    }

    [Fact]
    public void BuildForCreate_IncludesParticipantBlock_WhenAny()
    {
        var participants = new[] { new ParticipantRecord { Name = "Bob", Email = "bob@x.com" } };
        var rec = MakeRecord(participants: participants);

        var d = _sut.BuildForCreate(rec, 30);
        d.BodyHtml.Should().Contain("Bob");
        d.BodyHtml.Should().Contain("calimport:participants:start");
    }

    [Fact]
    public void BuildForUpdate_ReplacesBodyCompletely_DoesNotAccumulate()
    {
        // The destination body already holds a participants block from a prior sync (and Graph has stripped
        // the comment markers on save, so the stale table lingers without markers). An update must REPLACE
        // the body with a fresh render of the SOURCE — exactly ONE participants block + the source
        // description — never prepend another (the N-copies accumulation bug).
        var existingWithOldBlock =
            "<p>User text</p>\n<p><b>Participants (reference only — not invited):</b></p>\n" +
            "<table><tr><td>OLD</td></tr></table>";
        var participants = new[] { new ParticipantRecord { Name = "NewBob", Email = "newbob@x.com" } };
        var rec = MakeRecord(description: "Fresh desc", participants: participants);

        var d = _sut.BuildForUpdate(rec, 30, existingWithOldBlock);

        d.BodyHtml.Should().Contain("NewBob");
        d.BodyHtml.Should().Contain("Fresh desc");
        d.BodyHtml.Should().NotContain("OLD");        // the stale destination block is dropped
        d.BodyHtml.Should().NotContain("User text");  // the destination body is replaced completely
        System.Text.RegularExpressions.Regex.Matches(d.BodyHtml, "calimport:participants:start")
            .Count.Should().Be(1);                    // exactly one block — no accumulation
    }

    [Fact]
    public void BuildForCreate_AllDay_FlagPropagates()
    {
        var d = _sut.BuildForCreate(MakeRecord(isAllDay: true), 30);
        d.IsAllDay.Should().BeTrue();
    }

    [Fact]
    public void NullRecord_Throws()
    {
        Action act = () => _sut.BuildForCreate(null!, 30);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullRenderer_Throws()
    {
        Action act = () => new EventDraftBuilder(null!);
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("renderer");
    }

    [Fact]
    public void BuildForUpdate_RendersFreshBodyFromSource_NeverMerges()
    {
        // The update body is built from the SOURCE (BuildBodyForCreate), independent of whatever the
        // destination currently has — and the merge path is never used (Graph strips its markers).
        var rendererMock = new Mock<IParticipantRenderer>(MockBehavior.Strict);
        rendererMock
            .Setup(r => r.BuildBodyForCreate(It.IsAny<string>(), It.IsAny<IReadOnlyList<ParticipantRecord>>()))
            .Returns("fresh-body");

        var sut = new EventDraftBuilder(rendererMock.Object);
        var rec = MakeRecord(description: "Desc");

        var d = sut.BuildForUpdate(rec, 30, existingBodyHtml: "<p>whatever the destination had</p>");

        d.BodyHtml.Should().Be("fresh-body");
        rendererMock.Verify(
            r => r.BuildBodyForCreate("Desc", It.IsAny<IReadOnlyList<ParticipantRecord>>()), Times.Once);
        rendererMock.Verify(
            r => r.MergeIntoExistingBody(It.IsAny<string>(), It.IsAny<IReadOnlyList<ParticipantRecord>>()), Times.Never);
    }

    [Fact]
    public void BuildForCreate_WhitespaceOnlyTimeZone_FallsBackToUtc()
    {
        var rec = MakeRecord(tz: "   ");
        var d   = _sut.BuildForCreate(rec, 30);
        d.TimeZoneId.Should().Be("UTC");
    }

    [Fact]
    public void BuildForUpdate_NullRecord_Throws()
    {
        Action act = () => _sut.BuildForUpdate(null!, 30, existingBodyHtml: "");
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("record");
    }
}
