namespace ZyncMaster.Server;

// One side of a sync pair. Provider is "OutlookCom" (events pushed from a desktop
// device that reads Outlook Classic via COM) or "MicrosoftGraph" (events read by the
// server directly from a connected account). AccountRef identifies the connected
// account this endpoint belongs to ("default" / null for the single-account case).
public sealed record Endpoint
{
    public required string Provider { get; init; }
    public string? AccountRef { get; init; }
    public required string CalendarId { get; init; }
    public string CalendarName { get; init; } = "";
}

// A configured one-way mirror from Source to Destination. State is "active",
// "paused" or "disabled"; disabled pairs are left behind when an account is forgotten.
public sealed record SyncPair
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Endpoint Source { get; init; }
    public required Endpoint Destination { get; init; }
    public int IntervalMin { get; init; }
    public string State { get; init; } = "active";
    public DateTimeOffset? LastRunUtc { get; init; }
    public MirrorResult? LastResult { get; init; }
}

// Counts from a single mirror run, returned by push/run and stored as a pair's LastResult.
public sealed record MirrorResult
{
    public int Created { get; init; }
    public int Updated { get; init; }
    public int Deleted { get; init; }
    public int Skipped { get; init; }
    public List<string> Failures { get; init; } = new();

    // True when the run did not fully reconcile the window because a transient failure
    // (429 / timeout / network) forced the destructive orphan sweep to be skipped this run
    // (plan v2 §B-2). The applied upserts are durable; orphan cleanup is deferred to a
    // later run. Surfaced so the caller/panel can show "partial — will retry".
    public bool Partial { get; init; }
}

// Summary of a connected account exposed to the panel.
public sealed record AccountInfo
{
    public required string AccountRef { get; init; }
    public required string DisplayName { get; init; }
    public bool IsDefault { get; init; }
}
