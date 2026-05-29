namespace ZyncMaster.Engine;

public sealed record AccountInfo
{
    public string AccountRef { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public bool IsDefault { get; init; }
}
