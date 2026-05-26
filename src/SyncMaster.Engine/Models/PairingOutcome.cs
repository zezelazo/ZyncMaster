namespace SyncMaster.Engine;

public sealed record PairingOutcome
{
    public bool Success { get; init; }
    public string? ApiKey { get; init; }
    public string? Message { get; init; }
}
