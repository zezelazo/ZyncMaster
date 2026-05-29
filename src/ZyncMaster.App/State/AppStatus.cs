namespace ZyncMaster.App.State;

// Snapshot of the host state pushed to the web UI and used to drive the tray icon.
// It is intentionally a plain serializable record: the bridge serializes it straight
// to JSON for the web layer, and the tray reads Status to pick its icon.
public sealed record AppStatus
{
    // Coarse visual state for the tray icon and dashboard orb.
    public SyncStatus Status { get; init; } = SyncStatus.Idle;

    // True once a device API key is present (the device has been paired).
    public bool Paired { get; init; }

    // True while auto-sync is paused by the user (the loop honours this flag).
    public bool Paused { get; init; }

    // Short approval code shown during the pairing flow; null when not pairing.
    public string? PairingCode { get; init; }

    // True when the server has no Microsoft account connected yet.
    public bool NoConnectedAccount { get; init; }

    // Human-readable summary of the last cycle (e.g. "created 2, updated 1").
    public string? LastMessage { get; init; }

    // UTC timestamp of the last completed cycle; null before the first cycle.
    public DateTimeOffset? LastSyncUtc { get; init; }

    // Counts from the most recent successful push, for the dashboard summary.
    public int Created { get; init; }
    public int Updated { get; init; }
    public int Deleted { get; init; }
    public int Skipped { get; init; }
}
