namespace SyncMaster.Graph;

public sealed class CalendarTargetInfo
{
    public string Id          { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public bool   IsDefault   { get; init; }
    public string Owner       { get; init; } = "";
}
