using System;
using System.Collections.Generic;

namespace ZyncMaster.Core;

public sealed class AppointmentRecord
{
    // Stable identifier used by CalImport for upsert. Sourced from
    // Outlook's GlobalAppointmentID when available; otherwise a
    // deterministic UUID v5 derived from organizer + start UTC + subject.
    public string    Id             { get; init; } = "";

    public DateTime  Start          { get; init; }
    public int       Duration       { get; init; }  // minutes
    public bool      IsAllDay       { get; init; }
    public string    Subject        { get; init; } = "";
    public string    OrganizerName  { get; init; } = "";
    public string    OrganizerEmail { get; init; } = "";
    public bool      IsCancelled    { get; init; }

    // Complete mode only
    public DateTimeOffset StartOffset              { get; init; }
    public DateTimeOffset EndOffset                { get; init; }
    public string         StartTimeZoneId          { get; init; } = "";
    public string         StartTimeZoneDisplayName { get; init; } = "";
    public string         Description              { get; init; } = "";

    public IReadOnlyList<ParticipantRecord> Participants { get; init; }
        = Array.Empty<ParticipantRecord>();
}
