using System;
using System.Collections.Generic;

namespace ZyncMaster.Graph;

public sealed record MirrorOutcome
{
    public int Created { get; init; }
    public int Updated { get; init; }
    public int Deleted { get; init; }
    public int Skipped { get; init; }
    public IReadOnlyList<string> Failures { get; init; } = Array.Empty<string>();
    public bool HadFailures => Failures.Count > 0;
}
