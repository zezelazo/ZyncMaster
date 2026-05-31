namespace ZyncMaster.Server;

public sealed record SyncRequest
{
    public required System.Collections.Generic.List<ZyncMaster.Core.AppointmentRecord> Events { get; init; }
}

public sealed record SyncResponse
{
    public int Created { get; init; }
    public int Updated { get; init; }
    public int Deleted { get; init; }
    public int Skipped { get; init; }
    public System.Collections.Generic.List<string> Failures { get; init; } = new();

    // True when a transient failure forced the destructive orphan sweep to be skipped this
    // run (plan v2 §B-2). The device should retry; no data was deleted.
    public bool Partial { get; init; }
}

// Discriminated outcome for SyncService. NoAccount=true means the endpoint must
// return 409 (no Microsoft account connected); otherwise Response carries the counts.
public sealed record SyncOutcome
{
    public bool NoAccount { get; init; }
    public SyncResponse? Response { get; init; }
}
