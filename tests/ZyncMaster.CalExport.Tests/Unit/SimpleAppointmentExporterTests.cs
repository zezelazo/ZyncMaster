using System;
using System.Collections.Generic;
using ZyncMaster.CalExport;
using ZyncMaster.Core;
using FluentAssertions;
using Xunit;

namespace ZyncMaster.CalExport.Tests;

public sealed class SimpleAppointmentExporterTests
{
    private readonly SimpleAppointmentExporter _sut = new SimpleAppointmentExporter();

    private static ExportContext MakeContext(int year = 2025, int month = 5) =>
        new ExportContext
        {
            Year                 = year,
            Month                = month,
            MonthName            = MonthNames.Get(month),
            CalendarDisplayNames = new[] { "Work" },
            ExportedAt           = DateTimeOffset.Now,
        };

    private static AppointmentRecord MakeRecord(
        DateTime? start         = null,
        int       duration      = 60,
        bool      isAllDay      = false,
        string    subject       = "Test Event",
        string    organizerName = "Alice",
        string    organizerEmail = "alice@test.com",
        bool      isCancelled   = false) =>
        new AppointmentRecord
        {
            Start          = start ?? new DateTime(2025, 5, 15, 10, 0, 0),
            Duration       = duration,
            IsAllDay       = isAllDay,
            Subject        = subject,
            OrganizerName  = organizerName,
            OrganizerEmail = organizerEmail,
            IsCancelled    = isCancelled,
        };

    [Fact]
    public void NormalEvent_CorrectPipeDelimitedFormat()
    {
        var record = MakeRecord(new DateTime(2025, 5, 15, 10, 30, 0), duration: 60);
        var ctx    = MakeContext();

        var result = _sut.Serialize(new[] { record }, ctx);

        result.Should().Be("2025-05-15 | 10:30 | 1h 00m | Test Event | Alice <alice@test.com>");
    }

    [Fact]
    public void AllDayEvent_UsesAllDayForTimeAndDuration()
    {
        var record = MakeRecord(new DateTime(2025, 5, 15, 0, 0, 0), duration: 1440, isAllDay: true);
        var ctx    = MakeContext();

        var result = _sut.Serialize(new[] { record }, ctx);

        result.Should().Be("2025-05-15 | All day | All day | Test Event | Alice <alice@test.com>");
    }

    [Fact]
    public void CancelledEvent_EndsWithCancelado()
    {
        var record = MakeRecord(isCancelled: true);
        var ctx    = MakeContext();

        var result = _sut.Serialize(new[] { record }, ctx);

        result.Should().EndWith("| CANCELADO");
    }

    [Fact]
    public void NoOrganizerEmail_NoAngleBrackets()
    {
        var record = MakeRecord(organizerEmail: "");
        var ctx    = MakeContext();

        var result = _sut.Serialize(new[] { record }, ctx);

        result.Should().NotContain("<");
        result.Should().NotContain(">");
        result.Should().Contain("| Alice");
    }

    [Fact]
    public void WithOrganizerEmail_IncludesAngleBrackets()
    {
        var record = MakeRecord(organizerEmail: "alice@test.com");
        var ctx    = MakeContext();

        var result = _sut.Serialize(new[] { record }, ctx);

        result.Should().Contain("Alice <alice@test.com>");
    }

    [Fact]
    public void Duration_90Min_Formats1h30m()
    {
        var record = MakeRecord(duration: 90);
        var ctx    = MakeContext();

        var result = _sut.Serialize(new[] { record }, ctx);

        result.Should().Contain("1h 30m");
    }

    [Fact]
    public void Duration_60Min_Formats1h00m()
    {
        var record = MakeRecord(duration: 60);
        var ctx    = MakeContext();

        var result = _sut.Serialize(new[] { record }, ctx);

        result.Should().Contain("1h 00m");
    }

    [Fact]
    public void MultipleRecords_LinesJoinedWithNewline()
    {
        var r1  = MakeRecord(new DateTime(2025, 5, 1, 9, 0, 0),  subject: "First");
        var r2  = MakeRecord(new DateTime(2025, 5, 2, 10, 0, 0), subject: "Second");
        var ctx = MakeContext();

        var result = _sut.Serialize(new[] { r1, r2 }, ctx);

        var lines = result.Split('\n');
        lines.Should().HaveCount(2);
        lines[0].Should().Contain("First");
        lines[1].Should().Contain("Second");
    }

    [Fact]
    public void EmptyList_ReturnsEmptyString()
    {
        var ctx    = MakeContext();
        var result = _sut.Serialize(Array.Empty<AppointmentRecord>(), ctx);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Duration_30Min_Formats0h30m()
    {
        var record = MakeRecord(duration: 30);
        var result = _sut.Serialize(new[] { record }, MakeContext());
        result.Should().Contain("0h 30m");
    }

    [Fact]
    public void Duration_120Min_Formats2h00m()
    {
        var record = MakeRecord(duration: 120);
        var result = _sut.Serialize(new[] { record }, MakeContext());
        result.Should().Contain("2h 00m");
    }

    [Fact]
    public void Duration_5Min_FormatsWithLeadingZero()
    {
        var record = MakeRecord(duration: 5);
        var result = _sut.Serialize(new[] { record }, MakeContext());
        result.Should().Contain("0h 05m");
    }

    [Fact]
    public void FileSuffix_IsSimple()
    {
        _sut.FileSuffix.Should().Be("simple");
    }

    [Fact]
    public void FileExtension_IsTxt()
    {
        _sut.FileExtension.Should().Be("txt");
    }
}
