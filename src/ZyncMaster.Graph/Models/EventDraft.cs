using System;

namespace ZyncMaster.Graph;

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

    // The id of the sync PAIR that produced this event. Written as a SECOND single-value extended
    // property (CalImportPairId) alongside the per-event CalImportSourceId, so the destination
    // cleanup can enumerate and delete exactly the events THIS pair created — never another pair's
    // events that happen to share the same destination, and never the user's own events. Empty when
    // the writer is not pair-scoped (e.g. the device /sync path), in which case the property is
    // omitted and the event is simply not addressable by a pair cleanup.
    public string         PairId                     { get; init; } = "";
}
