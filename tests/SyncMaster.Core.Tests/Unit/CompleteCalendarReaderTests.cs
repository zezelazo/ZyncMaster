using System;
using FluentAssertions;
using SyncMaster.Core;
using Xunit;

namespace SyncMaster.Core.Tests.Unit;

public sealed class CompleteCalendarReaderTests
{
    private static CompleteCalendarReader BuildSut() => new CompleteCalendarReader();

    private const string ValidEventJson = @"{
      ""id"": ""abc-123"",
      ""subject"": ""Team Meeting"",
      ""isAllDay"": false,
      ""isCancelled"": false,
      ""start"": ""2025-05-15T10:00:00-05:00"",
      ""startUtc"": ""2025-05-15T15:00:00Z"",
      ""startTimeZoneId"": ""Eastern Standard Time"",
      ""startTimeZoneDisplayName"": ""(UTC-05:00) Eastern"",
      ""end"": ""2025-05-15T11:00:00-05:00"",
      ""endUtc"": ""2025-05-15T16:00:00Z"",
      ""durationMinutes"": 60,
      ""organizer"": { ""name"": ""Alice"", ""email"": ""alice@x.com"" },
      ""description"": ""Sync"",
      ""participants"": [
        { ""name"": ""Bob"", ""email"": ""bob@x.com"", ""type"": ""required"", ""response"": ""accepted"" }
      ]
    }";

    private const string SecondValidEventJson = @"{
      ""id"": ""xyz-999"",
      ""subject"": ""Another Meeting"",
      ""isAllDay"": false,
      ""isCancelled"": false,
      ""start"": ""2025-05-16T09:00:00-05:00"",
      ""startTimeZoneId"": ""Eastern Standard Time"",
      ""startTimeZoneDisplayName"": ""(UTC-05:00) Eastern"",
      ""end"": ""2025-05-16T10:00:00-05:00"",
      ""durationMinutes"": 60,
      ""organizer"": { ""name"": ""Alice"", ""email"": ""alice@x.com"" },
      ""description"": ""Other"",
      ""participants"": []
    }";

    private static string Wrap(string events) => $@"{{
      ""exportedAt"": ""2025-05-20T12:00:00-05:00"",
      ""period"": {{ ""year"": 2025, ""month"": 5, ""monthName"": ""May"" }},
      ""calendars"": [""Work""],
      ""events"": [{events}]
    }}";

    private static string WrapMany(params string[] events) => $@"{{
      ""exportedAt"": ""2025-05-20T12:00:00-05:00"",
      ""period"": {{ ""year"": 2025, ""month"": 5, ""monthName"": ""May"" }},
      ""calendars"": [""Work""],
      ""events"": [{string.Join(",", events)}]
    }}";

    [Fact]
    public void Parse_NotJson_Throws()
    {
        Action act = () => BuildSut().Parse("{ not json ]]]");
        act.Should().Throw<CalendarReadException>().WithMessage("*not valid JSON*");
    }

    [Fact]
    public void Parse_MissingEvents_Throws()
    {
        Action act = () => BuildSut().Parse(@"{ ""exportedAt"": ""2025-05-20T12:00:00Z"", ""period"": { ""year"": 2025, ""month"": 5 } }");
        act.Should().Throw<CalendarReadException>().WithMessage("*missing 'events'*");
    }

    [Fact]
    public void Parse_EventMissingId_Throws()
    {
        var bad = ValidEventJson.Replace(@"""id"": ""abc-123"",", "");
        Action act = () => BuildSut().Parse(Wrap(bad));
        act.Should().Throw<CalendarReadException>().WithMessage("*older CalExport*");
    }

    [Fact]
    public void Parse_EventEmptyId_Throws()
    {
        var bad = ValidEventJson.Replace(@"""id"": ""abc-123""", @"""id"": """"");
        Action act = () => BuildSut().Parse(Wrap(bad));
        act.Should().Throw<CalendarReadException>().WithMessage("*empty 'id'*");
    }

    [Fact]
    public void Parse_Valid_PopulatesEventFields()
    {
        var result = BuildSut().Parse(Wrap(ValidEventJson));

        result.Events.Should().HaveCount(1);
        var ev = result.Events[0];

        ev.Id.Should().Be(OccurrenceId.For("abc-123", ev.StartOffset));
        ev.Id.Should().NotBe("abc-123");
        ev.Subject.Should().Be("Team Meeting");
        ev.OrganizerName.Should().Be("Alice");
        ev.OrganizerEmail.Should().Be("alice@x.com");
        ev.Duration.Should().Be(60);
        ev.IsAllDay.Should().BeFalse();
        ev.IsCancelled.Should().BeFalse();
        ev.Description.Should().Be("Sync");
        ev.StartTimeZoneId.Should().Be("Eastern Standard Time");
        ev.StartTimeZoneDisplayName.Should().Be("(UTC-05:00) Eastern");
        ev.StartOffset.UtcDateTime.Should().Be(new DateTime(2025, 5, 15, 15, 0, 0));
        ev.EndOffset.UtcDateTime.Should().Be(new DateTime(2025, 5, 15, 16, 0, 0));

        ev.Participants.Should().HaveCount(1);
        ev.Participants[0].Name.Should().Be("Bob");
        ev.Participants[0].Email.Should().Be("bob@x.com");
        ev.Participants[0].Type.Should().Be("required");
        ev.Participants[0].Response.Should().Be("accepted");
    }

    [Fact]
    public void Parse_NoParticipants_ReturnsEmptyList()
    {
        var noParts = ValidEventJson.Replace(
            @"""participants"": [
        { ""name"": ""Bob"", ""email"": ""bob@x.com"", ""type"": ""required"", ""response"": ""accepted"" }
      ]",
            @"""participants"": []");
        var result = BuildSut().Parse(Wrap(noParts));
        result.Events[0].Participants.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ParticipantsPropertyAbsent_ReturnsEmptyList()
    {
        var noParts = ValidEventJson.Replace(
            @",
      ""participants"": [
        { ""name"": ""Bob"", ""email"": ""bob@x.com"", ""type"": ""required"", ""response"": ""accepted"" }
      ]",
            "");
        var result = BuildSut().Parse(Wrap(noParts));
        result.Events[0].Participants.Should().BeEmpty();
    }

    [Fact]
    public void Parse_CancelledEvent_IsCancelledTrue()
    {
        var cancelled = ValidEventJson.Replace(@"""isCancelled"": false", @"""isCancelled"": true");
        BuildSut().Parse(Wrap(cancelled)).Events[0].IsCancelled.Should().BeTrue();
    }

    [Fact]
    public void Parse_AllDayEvent_IsAllDayTrue()
    {
        var allDay = ValidEventJson.Replace(@"""isAllDay"": false", @"""isAllDay"": true");
        BuildSut().Parse(Wrap(allDay)).Events[0].IsAllDay.Should().BeTrue();
    }

    [Fact]
    public void Parse_InvalidStartTimestamp_Throws()
    {
        var bad = ValidEventJson.Replace(@"""2025-05-15T10:00:00-05:00""", @"""not-a-date""");
        Action act = () => BuildSut().Parse(Wrap(bad));
        act.Should().Throw<CalendarReadException>().WithMessage("*not a valid ISO 8601*");
    }

    [Fact]
    public void Parse_MissingSubject_Throws()
    {
        var bad = ValidEventJson.Replace(@"""subject"": ""Team Meeting"",", "");
        Action act = () => BuildSut().Parse(Wrap(bad));
        act.Should().Throw<CalendarReadException>()
           .WithMessage("*events[0]*missing required field 'subject'*");
    }

    [Fact]
    public void Parse_MissingIsAllDay_Throws()
    {
        var bad = ValidEventJson.Replace(@"""isAllDay"": false,", "");
        Action act = () => BuildSut().Parse(Wrap(bad));
        act.Should().Throw<CalendarReadException>()
           .WithMessage("*events[0]*missing required field 'isAllDay'*");
    }

    [Fact]
    public void Parse_MissingDurationMinutes_Throws()
    {
        var bad = ValidEventJson.Replace(@"""durationMinutes"": 60,", "");
        Action act = () => BuildSut().Parse(Wrap(bad));
        act.Should().Throw<CalendarReadException>()
           .WithMessage("*events[0]*missing required field 'durationMinutes'*");
    }

    [Fact]
    public void Parse_EmptySubject_OK()
    {
        var emptySubj = ValidEventJson.Replace(@"""subject"": ""Team Meeting""", @"""subject"": """"");
        BuildSut().Parse(Wrap(emptySubj)).Events[0].Subject.Should().Be("");
    }

    [Fact]
    public void Parse_RecurringOccurrences_SameIdDifferentStart_DistinctIds()
    {
        var occurrence2 = SecondValidEventJson.Replace(@"""id"": ""xyz-999""", @"""id"": ""abc-123""");
        var result = BuildSut().Parse(WrapMany(ValidEventJson, occurrence2));

        result.Events.Should().HaveCount(2);
        result.Events[0].Id.Should().NotBeNullOrEmpty();
        result.Events[0].Id.Should().NotBe(result.Events[1].Id);
    }

    [Fact]
    public void Parse_PeriodLabel_FromMonthNameAndYear()
    {
        var result = BuildSut().Parse(Wrap(ValidEventJson));
        result.PeriodLabel.Should().Be("May 2025");
    }

    [Fact]
    public void Parse_Null_Throws()
    {
        Action act = () => BuildSut().Parse(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
