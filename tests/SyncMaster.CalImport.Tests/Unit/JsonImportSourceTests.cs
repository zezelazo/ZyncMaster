using System;
using System.Collections.Generic;
using System.Text;
using FluentAssertions;
using Moq;
using SyncMaster.CalImport;
using SyncMaster.Core;
using Xunit;

namespace SyncMaster.CalImport.Tests;

public sealed class JsonImportSourceTests
{
    private readonly Mock<IFileSystem> _fs = new Mock<IFileSystem>();
    private JsonImportSource BuildSut() => new JsonImportSource(_fs.Object);

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

    private void Setup(string content)
    {
        _fs.Setup(f => f.FileExists("x.json")).Returns(true);
        _fs.Setup(f => f.ReadAllText("x.json")).Returns(content);
    }

    [Fact]
    public void Load_FileMissing_Throws()
    {
        _fs.Setup(f => f.FileExists("x.json")).Returns(false);
        Action act = () => BuildSut().Load("x.json");
        act.Should().Throw<ImportSourceException>().WithMessage("*not found*");
    }

    [Fact]
    public void Load_NotJson_Throws()
    {
        Setup("{ not json ]]]");
        Action act = () => BuildSut().Load("x.json");
        act.Should().Throw<ImportSourceException>().WithMessage("*not valid JSON*");
    }

    [Fact]
    public void Load_MissingEvents_Throws()
    {
        Setup(@"{ ""exportedAt"": ""2025-05-20T12:00:00Z"", ""period"": { ""year"": 2025, ""month"": 5 } }");
        Action act = () => BuildSut().Load("x.json");
        act.Should().Throw<ImportSourceException>().WithMessage("*missing 'events'*");
    }

    [Fact]
    public void Load_EventMissingId_Throws()
    {
        var bad = ValidEventJson.Replace(@"""id"": ""abc-123"",", "");
        Setup(Wrap(bad));
        Action act = () => BuildSut().Load("x.json");
        act.Should().Throw<ImportSourceException>().WithMessage("*older CalExport*");
    }

    [Fact]
    public void Load_Valid_PopulatesPayloadHeader()
    {
        Setup(Wrap(ValidEventJson));
        var p = BuildSut().Load("x.json");

        p.Year.Should().Be(2025);
        p.Month.Should().Be(5);
        p.MonthName.Should().Be("May");
        p.Calendars.Should().BeEquivalentTo(new[] { "Work" });
        p.Events.Should().HaveCount(1);
    }

    [Fact]
    public void Load_Valid_PopulatesEventFields()
    {
        Setup(Wrap(ValidEventJson));
        var p = BuildSut().Load("x.json");

        var ev = p.Events[0];
        // Id is the per-occurrence key derived from raw id + start, not the raw id itself.
        ev.Id.Should().Be(SourceIdFactory.For("abc-123", ev.StartOffset));
        ev.Id.Should().NotBe("abc-123");
        ev.Subject.Should().Be("Team Meeting");
        ev.OrganizerName.Should().Be("Alice");
        ev.OrganizerEmail.Should().Be("alice@x.com");
        ev.Duration.Should().Be(60);
        ev.IsAllDay.Should().BeFalse();
        ev.IsCancelled.Should().BeFalse();
        ev.Description.Should().Be("Sync");
        ev.StartTimeZoneId.Should().Be("Eastern Standard Time");
        ev.StartOffset.UtcDateTime.Should().Be(new DateTime(2025, 5, 15, 15, 0, 0));
        ev.EndOffset.UtcDateTime.Should().Be(new DateTime(2025, 5, 15, 16, 0, 0));

        ev.Participants.Should().HaveCount(1);
        ev.Participants[0].Name.Should().Be("Bob");
        ev.Participants[0].Type.Should().Be("required");
        ev.Participants[0].Response.Should().Be("accepted");
    }

    [Fact]
    public void Load_NoParticipants_ReturnsEmptyList()
    {
        var noParts = ValidEventJson.Replace(
            @"""participants"": [
        { ""name"": ""Bob"", ""email"": ""bob@x.com"", ""type"": ""required"", ""response"": ""accepted"" }
      ]",
            @"""participants"": []");
        Setup(Wrap(noParts));
        var p = BuildSut().Load("x.json");

        p.Events[0].Participants.Should().BeEmpty();
    }

    [Fact]
    public void Load_ParticipantsPropertyAbsent_ReturnsEmptyList()
    {
        var noParts = ValidEventJson.Replace(
            @",
      ""participants"": [
        { ""name"": ""Bob"", ""email"": ""bob@x.com"", ""type"": ""required"", ""response"": ""accepted"" }
      ]",
            "");
        Setup(Wrap(noParts));
        var p = BuildSut().Load("x.json");

        p.Events[0].Participants.Should().BeEmpty();
    }

    [Fact]
    public void Load_CancelledEvent_IsCancelledTrue()
    {
        var cancelled = ValidEventJson.Replace(@"""isCancelled"": false", @"""isCancelled"": true");
        Setup(Wrap(cancelled));

        BuildSut().Load("x.json").Events[0].IsCancelled.Should().BeTrue();
    }

    [Fact]
    public void Load_AllDayEvent_IsAllDayTrue()
    {
        var allDay = ValidEventJson.Replace(@"""isAllDay"": false", @"""isAllDay"": true");
        Setup(Wrap(allDay));

        BuildSut().Load("x.json").Events[0].IsAllDay.Should().BeTrue();
    }

    [Fact]
    public void Load_InvalidStartTimestamp_Throws()
    {
        var bad = ValidEventJson.Replace(@"""2025-05-15T10:00:00-05:00""", @"""not-a-date""");
        Setup(Wrap(bad));
        Action act = () => BuildSut().Load("x.json");
        act.Should().Throw<ImportSourceException>().WithMessage("*not a valid ISO 8601*");
    }

    [Fact]
    public void Load_DuplicateOccurrences_SameIdAndStart_Throws()
    {
        // Same source id AND same start = the same occurrence listed twice (data error).
        Setup(WrapMany(ValidEventJson, ValidEventJson));

        Action act = () => BuildSut().Load("x.json");
        act.Should().Throw<ImportSourceException>()
           .WithMessage("*Duplicate occurrences*");
    }

    [Fact]
    public void Load_RecurringOccurrences_SameIdDifferentStart_LoadsDistinctIds()
    {
        // Outlook shares one GlobalAppointmentID across every occurrence of a recurring
        // series. Same raw id with different starts must load as distinct, unique events.
        var occurrence2 = SecondValidEventJson.Replace(@"""id"": ""xyz-999""", @"""id"": ""abc-123""");
        Setup(WrapMany(ValidEventJson, occurrence2));

        var p = BuildSut().Load("x.json");

        p.Events.Should().HaveCount(2);
        p.Events[0].Id.Should().NotBeNullOrEmpty();
        p.Events[0].Id.Should().NotBe(p.Events[1].Id);
    }

    [Fact]
    public void Load_MissingSubject_Throws()
    {
        var bad = ValidEventJson.Replace(@"""subject"": ""Team Meeting"",", "");
        Setup(Wrap(bad));
        Action act = () => BuildSut().Load("x.json");
        act.Should().Throw<ImportSourceException>()
           .WithMessage("*events[0]*missing required field 'subject'*");
    }

    [Fact]
    public void Load_MissingIsAllDay_Throws()
    {
        var bad = ValidEventJson.Replace(@"""isAllDay"": false,", "");
        Setup(Wrap(bad));
        Action act = () => BuildSut().Load("x.json");
        act.Should().Throw<ImportSourceException>()
           .WithMessage("*events[0]*missing required field 'isAllDay'*");
    }

    [Fact]
    public void Load_MissingDurationMinutes_Throws()
    {
        var bad = ValidEventJson.Replace(@"""durationMinutes"": 60,", "");
        Setup(Wrap(bad));
        Action act = () => BuildSut().Load("x.json");
        act.Should().Throw<ImportSourceException>()
           .WithMessage("*events[0]*missing required field 'durationMinutes'*");
    }

    [Fact]
    public void Load_NullIsAllDay_Throws()
    {
        var bad = ValidEventJson.Replace(@"""isAllDay"": false,", @"""isAllDay"": null,");
        Setup(Wrap(bad));
        Action act = () => BuildSut().Load("x.json");
        act.Should().Throw<ImportSourceException>()
           .WithMessage("*events[0]*missing required field 'isAllDay'*");
    }

    [Fact]
    public void Load_NullDurationMinutes_Throws()
    {
        var bad = ValidEventJson.Replace(@"""durationMinutes"": 60,", @"""durationMinutes"": null,");
        Setup(Wrap(bad));
        Action act = () => BuildSut().Load("x.json");
        act.Should().Throw<ImportSourceException>()
           .WithMessage("*events[0]*missing required field 'durationMinutes'*");
    }

    [Fact]
    public void Load_EmptySubject_OK()
    {
        var emptySubj = ValidEventJson.Replace(@"""subject"": ""Team Meeting""", @"""subject"": """"");
        Setup(Wrap(emptySubj));
        var p = BuildSut().Load("x.json");
        p.Events[0].Subject.Should().Be("");
    }

    [Fact]
    public void Load_MissingPeriod_Throws()
    {
        var content = @"{
          ""exportedAt"": ""2025-05-20T12:00:00-05:00"",
          ""calendars"": [""Work""],
          ""events"": []
        }";
        Setup(content);
        Action act = () => BuildSut().Load("x.json");
        act.Should().Throw<ImportSourceException>()
           .WithMessage("*missing required 'period'*");
    }

    [Fact]
    public void Load_MissingYear_Throws()
    {
        var content = @"{
          ""exportedAt"": ""2025-05-20T12:00:00-05:00"",
          ""period"": { ""month"": 5 },
          ""calendars"": [""Work""],
          ""events"": []
        }";
        Setup(content);
        Action act = () => BuildSut().Load("x.json");
        act.Should().Throw<ImportSourceException>()
           .WithMessage("*period.year*missing*");
    }

    [Fact]
    public void Load_InvalidYear_NegativeThrows()
    {
        var content = @"{
          ""exportedAt"": ""2025-05-20T12:00:00-05:00"",
          ""period"": { ""year"": -1, ""month"": 5 },
          ""calendars"": [""Work""],
          ""events"": []
        }";
        Setup(content);
        Action act = () => BuildSut().Load("x.json");
        act.Should().Throw<ImportSourceException>()
           .WithMessage("*period.year must be >= 1*");
    }

    [Fact]
    public void Load_InvalidYear_ZeroThrows()
    {
        var content = @"{
          ""exportedAt"": ""2025-05-20T12:00:00-05:00"",
          ""period"": { ""year"": 0, ""month"": 5 },
          ""calendars"": [""Work""],
          ""events"": []
        }";
        Setup(content);
        Action act = () => BuildSut().Load("x.json");
        act.Should().Throw<ImportSourceException>()
           .WithMessage("*period.year must be >= 1*");
    }

    [Fact]
    public void Load_InvalidMonth_ThirteenThrows()
    {
        var content = @"{
          ""exportedAt"": ""2025-05-20T12:00:00-05:00"",
          ""period"": { ""year"": 2025, ""month"": 13 },
          ""calendars"": [""Work""],
          ""events"": []
        }";
        Setup(content);
        Action act = () => BuildSut().Load("x.json");
        act.Should().Throw<ImportSourceException>()
           .WithMessage("*period.month must be between 1 and 12*");
    }

    [Fact]
    public void Load_InvalidMonth_ZeroThrows()
    {
        var content = @"{
          ""exportedAt"": ""2025-05-20T12:00:00-05:00"",
          ""period"": { ""year"": 2025, ""month"": 0 },
          ""calendars"": [""Work""],
          ""events"": []
        }";
        Setup(content);
        Action act = () => BuildSut().Load("x.json");
        act.Should().Throw<ImportSourceException>()
           .WithMessage("*period.month must be between 1 and 12*");
    }

    [Fact]
    public void Load_MissingExportedAt_Throws()
    {
        var content = @"{
          ""period"": { ""year"": 2025, ""month"": 5 },
          ""calendars"": [""Work""],
          ""events"": []
        }";
        Setup(content);
        Action act = () => BuildSut().Load("x.json");
        act.Should().Throw<ImportSourceException>()
           .WithMessage("*'exportedAt' is missing*");
    }

    [Fact]
    public void Load_InvalidExportedAt_Throws()
    {
        var content = @"{
          ""exportedAt"": ""not-a-date"",
          ""period"": { ""year"": 2025, ""month"": 5 },
          ""calendars"": [""Work""],
          ""events"": []
        }";
        Setup(content);
        Action act = () => BuildSut().Load("x.json");
        act.Should().Throw<ImportSourceException>()
           .WithMessage("*'exportedAt' is not a valid ISO 8601*");
    }
}
