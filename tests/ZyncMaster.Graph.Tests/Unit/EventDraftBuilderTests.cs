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
    public void BuildForUpdate_MergesIntoExistingBody()
    {
        var existing = "<p>User text</p>\n<!-- calimport:participants:start --><ul><li>OLD</li></ul><!-- calimport:participants:end -->";
        var participants = new[] { new ParticipantRecord { Name = "NewBob", Email = "newbob@x.com" } };
        var rec = MakeRecord(participants: participants);

        var d = _sut.BuildForUpdate(rec, 30, existing);

        d.BodyHtml.Should().Contain("User text");
        d.BodyHtml.Should().Contain("NewBob");
        d.BodyHtml.Should().NotContain("OLD");
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
    public void BuildForUpdate_NullExistingBodyHtml_PassesEmptyStringToRenderer()
    {
        var rendererMock = new Mock<IParticipantRenderer>(MockBehavior.Strict);
        rendererMock
            .Setup(r => r.MergeIntoExistingBody(It.IsAny<string>(), It.IsAny<IReadOnlyList<ParticipantRecord>>()))
            .Returns("merged-body");

        var sut = new EventDraftBuilder(rendererMock.Object);
        var rec = MakeRecord();

        var d = sut.BuildForUpdate(rec, 30, existingBodyHtml: null!);

        d.BodyHtml.Should().Be("merged-body");
        rendererMock.Verify(
            r => r.MergeIntoExistingBody("", It.IsAny<IReadOnlyList<ParticipantRecord>>()),
            Times.Once);
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
