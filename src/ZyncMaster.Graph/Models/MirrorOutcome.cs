using System;
using System.Collections.Generic;
using System.Linq;

namespace ZyncMaster.Graph;

public sealed record MirrorOutcome
{
    public int Created { get; init; }
    public int Updated { get; init; }
    public int Deleted { get; init; }
    public int Skipped { get; init; }

    // Typed per-item failures (plan v2 §B-3). Replaces the old bare-string list so callers
    // can reason about WHY an item failed (Transient vs UserRecoverable vs Fatal).
    public IReadOnlyList<MirrorFailure> Failures { get; init; } = Array.Empty<MirrorFailure>();

    public bool HadFailures => Failures.Count > 0;

    // True when at least one item failed transiently (429 / timeout / network). When set,
    // the payload that was applied may be incomplete, so the window sweep was SKIPPED this
    // run to avoid deleting the user's legitimate events (plan v2 §B-2).
    public bool HasTransientFailure => Failures.Any(f => f.Kind == SyncErrorKind.Transient);

    // True when the run did not fully reconcile the window: a transient failure forced the
    // destructive sweep to be skipped, so orphan cleanup is deferred to a later run.
    // Callers surface this as "partial — will retry".
    public bool Partial { get; init; }
}
