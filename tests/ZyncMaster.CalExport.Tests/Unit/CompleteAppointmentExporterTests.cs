using System;
using System.Collections.Generic;
using ZyncMaster.CalExport;
using ZyncMaster.Core;
using FluentAssertions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ZyncMaster.CalExport.Tests;

public sealed class CompleteAppointmentExporterTests
{
    private readonly CompleteAppointmentExporter _sut = new CompleteAppointmentExporter();

    private static ExportContext MakeContext(
        int    year  = 2025,
        int    month = 5,
        string[]? calendarNames = null) =>
        new ExportContext
        {
            Year                 = year,
            Month                = month,
            MonthName            = MonthNames.Get(month),
            CalendarDisplayNames = calendarNames ?? new[] { "Work" },
            ExportedAt           = new DateTimeOffset(2025, 5, 20, 12, 0, 0, TimeSpan.FromHours(-5)),
        };

    private static AppointmentRecord MakeRecord(
        bool   isCancelled   = false,
        string subject       = "Team Meeting",
        string id            = "id-fixture-1",
        IReadOnlyList<ParticipantRecord>? participants = null)
    {
        var start  = new DateTime(2025, 5, 15, 10, 0, 0);
        var offset = TimeSpan.FromHours(-5);
        return new AppointmentRecord
        {
            Id                       = id,
            Start                    = start,
            Duration                 = 60,
            IsAllDay                 = false,
            Subject                  = subject,
            OrganizerName            = "Alice",
            OrganizerEmail           = "alice@test.com",
            IsCancelled              = isCancelled,
            StartOffset              = new DateTimeOffset(start, offset),
            EndOffset                = new DateTimeOffset(start.AddHours(1), offset),
            StartTimeZoneId          = "Eastern Standard Time",
            StartTimeZoneDisplayName = "(UTC-05:00) Eastern Time",
            Description              = "Weekly sync",
            Participants             = participants ?? Array.Empty<ParticipantRecord>(),
        };
    }

    [Fact]
    public void Serialize_ReturnsValidJson()
    {
        var result = _sut.Serialize(new[] { MakeRecord() }, MakeContext());

        JObject.Parse(result).Should().NotBeNull(); // no exception = valid JSON
    }

    [Fact]
    public void Serialize_ExportedAt_IsIso8601WithOffset()
    {
        var ctx    = MakeContext();
        var result = _sut.Serialize(new[] { MakeRecord() }, ctx);

        // Parse with DateParseHandling.None so that date-like strings are NOT
        // auto-converted to DateTime JValues (which would change the string format
        // when read back via JToken.ToString()).
        var json = JObject.Parse(result, new JsonLoadSettings { });
        using var sr = new System.IO.StringReader(result);
        using var jr = new Newtonsoft.Json.JsonTextReader(sr) { DateParseHandling = DateParseHandling.None };
        var jsonRaw = JObject.Load(jr);

        var exportedAt = jsonRaw["exportedAt"]?.Value<string>();
        exportedAt.Should().NotBeNull();
        // Must be ISO 8601 date+time format
        exportedAt.Should().MatchRegex(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}");
    }

    [Fact]
    public void Serialize_PeriodHasCorrectValues()
    {
        var ctx    = MakeContext(year: 2025, month: 3);
        var result = _sut.Serialize(new[] { MakeRecord() }, ctx);
        var json   = JObject.Parse(result);

        json["period"]!["year"]!.Value<int>().Should().Be(2025);
        json["period"]!["month"]!.Value<int>().Should().Be(3);
        json["period"]!["monthName"]!.Value<string>().Should().Be("March");
    }

    [Fact]
    public void Serialize_CalendarsFieldPresent()
    {
        var ctx    = MakeContext(calendarNames: new[] { "Work", "Personal" });
        var result = _sut.Serialize(new[] { MakeRecord() }, ctx);
        var json   = JObject.Parse(result);

        var calendars = json["calendars"] as JArray;
        calendars.Should().NotBeNull();
        calendars!.Count.Should().Be(2);
    }

    [Fact]
    public void Serialize_EventsArrayHasCorrectCount()
    {
        var records = new[] { MakeRecord(subject: "A"), MakeRecord(subject: "B") };
        var result  = _sut.Serialize(records, MakeContext());
        var json    = JObject.Parse(result);

        var events = json["events"] as JArray;
        events.Should().NotBeNull();
        events!.Count.Should().Be(2);
    }

    [Fact]
    public void Serialize_EventHasId()
    {
        var result = _sut.Serialize(new[] { MakeRecord(id: "stable-event-id") }, MakeContext());
        var json   = JObject.Parse(result);

        json["events"]![0]!["id"]!.Value<string>().Should().Be("stable-event-id");
    }

    [Fact]
    public void Serialize_EventHasAllRequiredFields()
    {
        var result = _sut.Serialize(new[] { MakeRecord() }, MakeContext());
        var json   = JObject.Parse(result);
        var ev     = json["events"]![0]!;

        ev["id"].Should().NotBeNull();
        ev["subject"].Should().NotBeNull();
        ev["isAllDay"].Should().NotBeNull();
        ev["isCancelled"].Should().NotBeNull();
        ev["start"].Should().NotBeNull();
        ev["startUtc"].Should().NotBeNull();
        ev["startTimeZoneId"].Should().NotBeNull();
        ev["startTimeZoneDisplayName"].Should().NotBeNull();
        ev["end"].Should().NotBeNull();
        ev["endUtc"].Should().NotBeNull();
        ev["durationMinutes"].Should().NotBeNull();
        ev["organizer"].Should().NotBeNull();
        ev["description"].Should().NotBeNull();
        ev["participants"].Should().NotBeNull();
    }

    [Fact]
    public void Serialize_IsCancelledFalse_ForNormalEvent()
    {
        var result = _sut.Serialize(new[] { MakeRecord(isCancelled: false) }, MakeContext());
        var json   = JObject.Parse(result);

        json["events"]![0]!["isCancelled"]!.Value<bool>().Should().BeFalse();
    }

    [Fact]
    public void Serialize_IsCancelledTrue_ForCancelledEvent()
    {
        var result = _sut.Serialize(new[] { MakeRecord(isCancelled: true) }, MakeContext());
        var json   = JObject.Parse(result);

        json["events"]![0]!["isCancelled"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public void Serialize_ParticipantsMappedCorrectly()
    {
        var participants = new[]
        {
            new ParticipantRecord { Name = "Bob", Email = "bob@test.com", Type = "required", Response = "accepted" },
        };
        var result = _sut.Serialize(new[] { MakeRecord(participants: participants) }, MakeContext());
        var json   = JObject.Parse(result);
        var p      = json["events"]![0]!["participants"]![0]!;

        p["name"]!.Value<string>().Should().Be("Bob");
        p["email"]!.Value<string>().Should().Be("bob@test.com");
        p["type"]!.Value<string>().Should().Be("required");
        p["response"]!.Value<string>().Should().Be("accepted");
    }

    [Fact]
    public void Serialize_EmptyEventsList_ReturnsValidJsonWithEmptyArray()
    {
        var result = _sut.Serialize(Array.Empty<AppointmentRecord>(), MakeContext());
        var json   = JObject.Parse(result);

        var events = json["events"] as JArray;
        events.Should().NotBeNull();
        events!.Count.Should().Be(0);
    }
}
