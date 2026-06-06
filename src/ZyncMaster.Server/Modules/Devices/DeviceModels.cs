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

    // FIX 1 (account-takeover via anonymous pairing) — PKCE proof-of-initiator. /api/pair/start
    // mints a high-entropy secret VERIFIER, returns it only to the caller that started the
    // pairing, and stores ONLY its SHA-256 here (the clear verifier never touches the DB). The
    // one-time api key is handed out by /api/pair/complete only when the caller proves possession
    // of the verifier whose hash matches. This binds completion to the initiator: an attacker who
    // knows a victim's pairingId (or grinds them) cannot complete the handshake without the
    // verifier, so the device-code takeover (induce the victim to approve, then complete with the
    // attacker's pairingId) is closed. Nullable for forward/backward compat: pre-FIX-1 rows have
    // no hash and CompletePairingAsync treats a null hash as "no PKCE bound" (legacy behaviour),
    // but every row minted by the patched StartPairingAsync carries one.
    public string? VerifierHash { get; init; }
}

public sealed record PairStartResult
{
    public required string PairingId { get; init; }
    public required string Code { get; init; }

    // FIX 1 — the clear PKCE verifier, returned ONCE to the initiating caller. The caller must
    // keep it and present it to /api/pair/complete to claim the api key. Never persisted in the
    // clear (only its hash is stored on the pending row).
    public required string Verifier { get; init; }
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
