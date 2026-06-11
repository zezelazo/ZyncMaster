using System;

namespace ZyncMaster.Graph;

// The WHITELIST payload of a replica (spec §3/§12): start/end (same duration), showAs, and the
// user's MANUAL mask title — nothing else, ever. This type deliberately CANNOT represent a
// body, participants, location, organizer, categories, sensitivity, attachments or sibling
// replicas: the privacy guarantee is enforced by construction, and the reflection test
// ReplicaDraft_property_whitelist_is_exact freezes the property set.
public sealed class ReplicaDraft
{
    public string         MaskTitle     { get; init; } = "";
    public DateTimeOffset Start         { get; init; }
    public DateTimeOffset End           { get; init; }
    public string         TimeZoneId    { get; init; } = "UTC";
    public bool           IsAllDay      { get; init; }
    public string         ShowAs        { get; init; } = "busy";

    // Opaque stable id (OccurrenceId family) written into the ZmReplicaOf extended property.
    // It carries NO account identity (no email, no domain, no display name) — invariant 5.
    public string         SourceEventId { get; init; } = "";
}
