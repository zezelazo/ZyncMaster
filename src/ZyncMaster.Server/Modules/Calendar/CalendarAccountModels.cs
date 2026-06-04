namespace ZyncMaster.Server;

// Models for the per-user pool of connected calendar accounts.

// Protocol family used to reach a connected calendar account.
public enum AccountKind
{
    Graph,
    Google,
    OutlookCom,
}

// Access level granted by the connected calendar account.
public enum AccountScope
{
    Read,
    ReadWrite,
}

// A calendar account connected by a user. Part of the per-user account pool. The refresh
// token is never carried on this record — it lives encrypted in the store and is fetched
// on demand via ICalendarAccountStore.GetRefreshTokenAsync.
public sealed record CalendarAccount
{
    public required string Id { get; init; }
    public required string UserId { get; init; }
    public required AccountKind Kind { get; init; }
    public required string Provider { get; init; }
    public required string AccountEmail { get; init; }
    public string? Authority { get; init; }
    public AccountScope Scope { get; init; }
    public string? DeviceId { get; init; }
    public string? DisplayName { get; init; }
    public DateTimeOffset ConnectedAt { get; init; }
    public string Status { get; init; } = "active";
}
