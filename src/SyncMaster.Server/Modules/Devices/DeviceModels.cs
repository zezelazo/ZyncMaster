namespace SyncMaster.Server;

public sealed record Device
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ApiKeyHash { get; init; }
    public string? TargetCalendarId { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset? LastSeenUtc { get; init; }
}

public sealed record PendingPairing
{
    public required string PairingId { get; init; }
    public required string DeviceName { get; init; }
    public required string Code { get; init; }
    public bool Approved { get; init; }
    public string? ApprovedDeviceId { get; init; }
    public string? OneTimeApiKey { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
}
