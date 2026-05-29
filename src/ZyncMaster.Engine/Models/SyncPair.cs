using System;

namespace ZyncMaster.Engine;

// A configured mirror between two endpoints. State is "active" | "paused" | "disabled".
public sealed record SyncPair
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public Endpoint Source { get; init; } = new();
    public Endpoint Destination { get; init; } = new();
    public int IntervalMin { get; init; }
    public string State { get; init; } = "";
    public DateTimeOffset? LastRunUtc { get; init; }
    public MirrorResult? LastResult { get; init; }
}
