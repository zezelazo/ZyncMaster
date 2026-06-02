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

    // Public, non-secret API-key lookup handle (§A-3). See DeviceRow.KeyId. Null for legacy keys.
    public string? KeyId { get; init; }

    // Capability flags + lease (Track B Phase 3).
    public string Platform { get; init; } = "windows";
    public bool HasOutlookCom { get; init; }
    public string? AppVersion { get; init; }
    public DateTimeOffset? LeaseUntil { get; init; }
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

// §A-2 — result of a brokered device registration. ApiKey is the one-time full "keyId.secret"
// string (only its hash is persisted); the caller must store it because it is unrecoverable.
public sealed record DeviceRegisterResult
{
    public required string DeviceId { get; init; }
    public required string ApiKey { get; init; }
    public DateTimeOffset LeaseUntil { get; init; }
}

public sealed record DeviceHeartbeatResult
{
    public DateTimeOffset LeaseUntil { get; init; }
}

// Result of a device self-rename. Echoes the resolved id + the persisted (trimmed) name so the
// App can update its UI without a follow-up read.
public sealed record DeviceRenameResult
{
    public required string DeviceId { get; init; }
    public required string Name { get; init; }
}
