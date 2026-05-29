namespace ZyncMaster.Engine;

public sealed record SyncResult
{
    public bool Skipped { get; init; }
    public string? SkipReason { get; init; }
    public SyncPushResult? Push { get; init; }
}
