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

    // COM device-pinning (Track B). The id of the device allowed to read this pair's COM source.
    // Null for non-COM pairs and for COM pairs not yet pinned. The scheduler filters on this so a
    // device only runs the COM pairs pinned to it; the App's "Sync now" compares it to its own
    // deviceId to decide between a local run and a request-sync signal.
    public string? PinnedDeviceId { get; init; }

    // Sync-now signal stamp (Track B). When newer than the value the pinned device's scheduler last
    // handled, the device runs the pair immediately (in addition to its due interval). Null = none.
    public DateTimeOffset? SyncRequestedUtc { get; init; }

    // Server-resolved presentation of the pinned device for the UI (Track B). Populated by the
    // server's pair listing for COM-pinned pairs; null/false otherwise. Not persisted on the device.
    public string? PinnedDeviceName { get; init; }
    public bool PinnedDeviceOnline { get; init; }
}
