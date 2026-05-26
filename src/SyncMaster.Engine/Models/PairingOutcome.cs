namespace SyncMaster.Engine;

public sealed record PairingOutcome
{
    public bool Success { get; init; }
    public string? ApiKey { get; init; }
    public string? Message { get; init; }

    // The short approval code shown to the user during interactive pairing, so the
    // host can display it. Null when the device was already paired (no new code issued).
    public string? Code { get; init; }
}
