namespace ZyncMaster.Engine;

public sealed record AccountInfo
{
    public string AccountRef { get; init; } = "";
    public string DisplayName { get; init; } = "";

    // The real mailbox email of the connected account, when known (empty for the no-email /
    // "default" case). Surfaced so the UI shows a humane label instead of the internal AccountRef.
    public string Email { get; init; } = "";
    public bool IsDefault { get; init; }
}
