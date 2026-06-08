namespace ZyncMaster.Engine;

public sealed record AccountInfo
{
    public string AccountRef { get; init; } = "";
    public string DisplayName { get; init; } = "";

    // The real mailbox email of the connected account, when known (empty for the no-email /
    // "default" case). Surfaced so the UI shows a humane label instead of the internal AccountRef.
    public string Email { get; init; } = "";
    public bool IsDefault { get; init; }

    // Consent level of this calendar account: "read" | "readwrite" ("" when unknown/legacy). The
    // wizard uses it to decide whether a read-only account needs a scope upgrade before it can be a
    // sync destination, and the Calendar list renders it as a per-account badge.
    public string Scope { get; init; } = "";
}
