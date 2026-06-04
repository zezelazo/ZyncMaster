using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace ZyncMaster.Core.Tests;

// The shared Simple-mode line format used by both CalExport (COM) and the Server (Graph source).
// These pin the exact column layout so the two export paths stay byte-identical.
public sealed class SimpleAppointmentFormatterTests
{
    [Fact]
    public void Empty_returns_empty_string()
    {
        SimpleAppointmentFormatter.Format(Array.Empty<AppointmentRecord>()).Should().Be("");
        SimpleAppointmentFormatter.Format(null!).Should().Be("");
    }

    [Fact]
    public void Timed_event_formats_date_time_duration_subject_creator_with_email()
    {
        var r = new AppointmentRecord
        {
            Start = new DateTime(2026, 6, 10, 9, 30, 0),
            Duration = 90,
            Subject = "Standup",
            OrganizerName = "Ana",
            OrganizerEmail = "ana@test",
        };

        SimpleAppointmentFormatter.FormatLine(r)
            .Should().Be("2026-06-10 | 09:30 | 1h 30m | Standup | Ana <ana@test>");
    }

    [Fact]
    public void Creator_without_email_omits_angle_brackets()
    {
        var r = new AppointmentRecord
        {
            Start = new DateTime(2026, 6, 10, 14, 5, 0),
            Duration = 60,
            Subject = "Solo",
            OrganizerName = "Bob",
            OrganizerEmail = "",
        };

        SimpleAppointmentFormatter.FormatLine(r)
            .Should().Be("2026-06-10 | 14:05 | 1h 00m | Solo | Bob");
    }

    [Fact]
    public void AllDay_event_uses_all_day_for_time_and_duration()
    {
        var r = new AppointmentRecord
        {
            Start = new DateTime(2026, 6, 12, 0, 0, 0),
            IsAllDay = true,
            Subject = "Holiday",
            OrganizerName = "HR",
        };

        SimpleAppointmentFormatter.FormatLine(r)
            .Should().Be("2026-06-12 | All day | All day | Holiday | HR");
    }

    [Fact]
    public void Cancelled_event_appends_marker()
    {
        var r = new AppointmentRecord
        {
            Start = new DateTime(2026, 6, 2, 8, 0, 0),
            Duration = 30,
            Subject = "Dropped",
            OrganizerName = "A",
            IsCancelled = true,
        };

        SimpleAppointmentFormatter.FormatLine(r)
            .Should().EndWith("| CANCELADO");
    }

    [Fact]
    public void Multiple_records_join_with_newline()
    {
        var records = new List<AppointmentRecord>
        {
            new() { Start = new DateTime(2026, 6, 1, 8, 0, 0), Duration = 60, Subject = "One", OrganizerName = "A" },
            new() { Start = new DateTime(2026, 6, 2, 9, 0, 0), Duration = 60, Subject = "Two", OrganizerName = "B" },
        };

        SimpleAppointmentFormatter.Format(records)
            .Should().Be("2026-06-01 | 08:00 | 1h 00m | One | A\n2026-06-02 | 09:00 | 1h 00m | Two | B");
    }
}
