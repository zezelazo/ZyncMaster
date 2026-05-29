namespace ZyncMaster.Engine;

public sealed record PairComplete
{
    public bool Approved { get; init; }
    public string? ApiKey { get; init; }
    public string? DeviceId { get; init; }
}
