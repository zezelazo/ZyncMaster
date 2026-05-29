using System.Collections.Generic;

namespace ZyncMaster.Engine;

public sealed record MirrorResult
{
    public int Created { get; init; }
    public int Updated { get; init; }
    public int Deleted { get; init; }
    public int Skipped { get; init; }
    public List<string> Failures { get; init; } = new();
}
