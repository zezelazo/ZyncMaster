using System.Collections.Generic;

namespace ZyncMaster.Engine;

// Result of a destination cleanup: how many of the pair's managed events were deleted from the
// previous destination, plus any per-event delete failures (best-effort; a retry re-enumerates).
public sealed record CleanupResult
{
    public int Deleted { get; init; }
    public List<string> Failures { get; init; } = new();
}
