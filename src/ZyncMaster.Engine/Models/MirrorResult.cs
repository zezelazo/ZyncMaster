using System.Collections.Generic;

namespace ZyncMaster.Engine;

public sealed record MirrorResult
{
    public int Created { get; init; }
    public int Updated { get; init; }
    public int Deleted { get; init; }
    public int Skipped { get; init; }
    public List<string> Failures { get; init; } = new();

    // True when this run was a no-op because ANOTHER run for the same pair was already in progress
    // (the server's run-lock returned 409 run_in_progress). It is NOT a failure — typically the
    // scheduled run and a manual "Sync now" raced; one wins the lock and mirrors, the other gets this.
    // Callers surface it as "already syncing", never as an error.
    public bool RunInProgress { get; init; }
}
