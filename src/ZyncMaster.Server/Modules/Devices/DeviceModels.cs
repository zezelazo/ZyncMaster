namespace ZyncMaster.Server;

public sealed record Device
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ApiKeyHash { get; init; }
    public string? TargetCalendarId { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset? LastSeenUtc { get; init; }

    // Owning user. Defaults to the seeded "default" user so existing single-user code
    // paths and tests keep working; the ApiKey auth handler reads it to attach the
    // "userId" claim to the authenticated principal.
    public string UserId { get; init; } = DefaultCurrentUserAccessor.DefaultUserId;
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

public sealed record PairStartResult
{
    public required string PairingId { get; init; }
    public required string Code { get; init; }
}

public sealed record PairCompleteResult
{
    public bool Approved { get; init; }
    public string? ApiKey { get; init; }
    public string? DeviceId { get; init; }
}
