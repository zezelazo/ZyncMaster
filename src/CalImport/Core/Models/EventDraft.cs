using System;

namespace SyncMaster.CalImport;

public sealed class EventDraft
{
    public string         Subject                    { get; init; } = "";
    public string         BodyHtml                   { get; init; } = "";
    public DateTimeOffset Start                      { get; init; }
    public DateTimeOffset End                        { get; init; }
    public string         TimeZoneId                 { get; init; } = "UTC";
    public bool           IsAllDay                   { get; init; }
    public int            ReminderMinutesBeforeStart { get; init; } = 30;
    public string         ExternalId                 { get; init; } = "";
}
