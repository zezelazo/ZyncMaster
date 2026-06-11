using System;

namespace ZyncMaster.Graph;

// Server-side projection of a Graph event used by the replica engine, the prefix rules and the
// unified day view. It DOES carry the subject (the server legitimately processes titles —
// design general §4.3); what can never happen is any of it flowing into a ReplicaDraft, which
// cannot represent it. Record: tests and the engine derive variants with `with`.
public sealed record SourceEventSnapshot
{
    public string GraphEventId { get; init; } = "";

    // Stable per-occurrence id: OccurrenceId.For(iCalUId ?? graph id, start). Same id family
    // the pair mirror uses, so replica links and mirror upserts share the dedupe semantics.
    public string StableId { get; init; } = "";

    public string Subject { get; init; } = "";
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    public string TimeZoneId { get; init; } = "UTC";
    public bool IsAllDay { get; init; }
    public string ShowAs { get; init; } = "busy";
    public bool IsCancelled { get; init; }
    public bool IsOrganizer { get; init; }

    // True when the event has at least one attendee. Only the BOOLEAN is surfaced — attendee
    // emails/names are never projected out of the Graph response.
    public bool HasAttendees { get; init; }

    // Managed marks (anti-loop §7): an event carrying EITHER mark is never a replication
    // source, never matches a prefix rule, and renders as a replica in the day view.
    public bool HasReplicaMark { get; init; }     // ZmReplicaOf present
    public bool HasCalImportMark { get; init; }   // CalImportSourceId present (pair mirror)

    // Rule id stamped by ZmRuleProcessed; "" = not processed (strip+fan-out runs at most once).
    public string RuleProcessedBy { get; init; } = "";
}
