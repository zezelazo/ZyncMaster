using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace SyncMaster.Core.Tests;

public sealed class AppointmentRecordTests
{
    [Fact]
    public void DefaultValues_StringsAreEmpty()
    {
        var record = new AppointmentRecord();

        record.Id.Should().BeEmpty();
        record.Subject.Should().BeEmpty();
        record.OrganizerName.Should().BeEmpty();
        record.OrganizerEmail.Should().BeEmpty();
        record.StartTimeZoneId.Should().BeEmpty();
        record.StartTimeZoneDisplayName.Should().BeEmpty();
        record.Description.Should().BeEmpty();
    }

    [Fact]
    public void DefaultValues_BoolsAreFalse()
    {
        var record = new AppointmentRecord();

        record.IsAllDay.Should().BeFalse();
        record.IsCancelled.Should().BeFalse();
    }

    [Fact]
    public void DefaultValues_ParticipantsIsEmptyNotNull()
    {
        var record = new AppointmentRecord();

        record.Participants.Should().NotBeNull();
        record.Participants.Should().BeEmpty();
    }

    [Fact]
    public void CanConstructWithAllProperties()
    {
        var start        = new DateTime(2025, 5, 15, 9, 0, 0);
        var startOffset  = new DateTimeOffset(start, TimeSpan.FromHours(-5));
        var endOffset    = startOffset.AddHours(1);
        var participants = new List<ParticipantRecord>
        {
            new ParticipantRecord { Name = "Alice", Email = "alice@x.com", Type = "required", Response = "accepted" },
        };

        var record = new AppointmentRecord
        {
            Id                       = "abc-123",
            Start                    = start,
            Duration                 = 60,
            IsAllDay                 = false,
            Subject                  = "Team Meeting",
            OrganizerName            = "Bob",
            OrganizerEmail           = "bob@x.com",
            IsCancelled              = false,
            StartOffset              = startOffset,
            EndOffset                = endOffset,
            StartTimeZoneId          = "Eastern Standard Time",
            StartTimeZoneDisplayName = "(UTC-05:00) Eastern Time",
            Description              = "Weekly sync",
            Participants             = participants,
        };

        record.Id.Should().Be("abc-123");
        record.Start.Should().Be(start);
        record.Duration.Should().Be(60);
        record.Subject.Should().Be("Team Meeting");
        record.OrganizerName.Should().Be("Bob");
        record.OrganizerEmail.Should().Be("bob@x.com");
        record.Participants.Should().HaveCount(1);
        record.Participants[0].Name.Should().Be("Alice");
    }
}
