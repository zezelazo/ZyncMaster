using System;

namespace ZyncMaster.Graph;

// Draft for an event the user creates in OUR UI on their own calendar (spec §4). Unlike
// ReplicaDraft this MAY carry body/location — they go ONLY to the origin event, never to any
// replica (the fan-out builds ReplicaDrafts, which cannot represent them).
public sealed class OriginEventDraft
{
    public string         Subject    { get; init; } = "";
    public string         BodyHtml   { get; init; } = "";
    public string         Location   { get; init; } = "";
    public DateTimeOffset Start      { get; init; }
    public DateTimeOffset End        { get; init; }
    public string         TimeZoneId { get; init; } = "UTC";
    public bool           IsAllDay   { get; init; }
    public string         ShowAs     { get; init; } = "busy";
}
