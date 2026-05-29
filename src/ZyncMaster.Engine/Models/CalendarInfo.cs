namespace ZyncMaster.Engine;

// A calendar exposed by an account (returned by GET /api/accounts/{accountRef}/calendars).
public sealed record CalendarInfo
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public bool IsDefault { get; init; }
    public string? Owner { get; init; }
}
