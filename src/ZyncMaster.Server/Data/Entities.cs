namespace ZyncMaster.Server.Data;

// EF row types. Kept as plain mutable POCOs (not the domain records) so EF change
// tracking works cleanly and the mapping to/from the domain types lives in the stores.

public sealed class UserRow
{
    public string Id { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Subject { get; set; } = "";
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }

    // Canonical account-level email across all linked identity logins. Distinct from the
    // legacy per-(Provider,Subject) Email above. Required.
    public string PrimaryEmail { get; set; } = "";

    // Subscription plan slug; null means "everything unlocked" (no plan gating).
    public string? Plan { get; set; }
}

public sealed class ConnectedAccountRow
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Provider { get; set; } = "";
    public string AccountRef { get; set; } = "";
    public string? DisplayName { get; set; }
    public string EncryptedRefreshToken { get; set; } = "";
    public DateTimeOffset ConnectedUtc { get; set; }
    public bool IsDefault { get; set; }
}

// Per-user calendar account pool row. The encrypted refresh token is stored inline (1:1
// with the account) rather than in a separate AccountTokenRow: there is exactly one live
// token per account, so rotation is a single atomic row update and disconnect a single
// delete — no second table to keep in sync. OutlookCom accounts keep this null (their key
// is DeviceId).
public sealed class CalendarAccountRow
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Provider { get; set; } = "";
    public string AccountEmail { get; set; } = "";
    public string? Authority { get; set; }
    public string Scope { get; set; } = "";
    public string? DeviceId { get; set; }
    public string? DisplayName { get; set; }
    public string? EncryptedRefreshToken { get; set; }
    public string Status { get; set; } = "active";
    public DateTimeOffset ConnectedAt { get; set; }
}

public sealed class DeviceRow
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string ApiKeyHash { get; set; } = "";
    public string? TargetCalendarId { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? LastSeenUtc { get; set; }

    // Public, non-secret API-key lookup handle (§A-3). The key is "keyId.secret"; this column
    // stores the keyId unhashed and indexed so an incoming key locates exactly ONE candidate
    // device (index seek) before the single PBKDF2 verify of its secret. Null for legacy keys
    // minted before §A-3 (those fall back to the scan path until the key is re-issued).
    public string? KeyId { get; set; }

    // Device capability flags + lease (Track B Phase 3). All nullable/defaulted so the
    // migration is purely additive and pre-Track-B rows keep working.
    public string Platform { get; set; } = "windows"; // "windows" | "macos" | "linux"
    public bool HasOutlookCom { get; set; }            // can read Outlook Classic via COM
    public string? AppVersion { get; set; }

    // When the App last claimed (or renewed) an active lease. While LeaseUntil > now the App
    // is considered to be running this device's syncs, so the server-side cron trigger skips
    // any pair owned by this user (see /api/sync/run-due). Null = no active lease.
    public DateTimeOffset? LeaseUntil { get; set; }
}

public sealed class PendingPairingRow
{
    public string PairingId { get; set; } = "";
    public string? UserId { get; set; }
    public string DeviceName { get; set; } = "";
    public string Code { get; set; } = "";
    public bool Approved { get; set; }
    public string? ApprovedDeviceId { get; set; }
    public string? OneTimeApiKey { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
}

public sealed class SyncPairRow
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public string SourceJson { get; set; } = "";
    public string DestinationJson { get; set; } = "";
    public int IntervalMin { get; set; }
    public string State { get; set; } = "active";
    public DateTimeOffset? LastRunUtc { get; set; }
    public string? LastResultJson { get; set; }
}

// Server-side run lock for a sync pair (plan v2 §B-1). Exactly one row per pair, keyed by
// PairId. Acquisition is an atomic `UPDATE ... WHERE PairId=@id AND LockedUntil < @now`
// guarded by rowsAffected==1 (or an INSERT when no row exists yet), so two concurrent
// executors (App tick + manual run, or overlapping ticks) can never both run the mirror for
// the same pair. The lock is time-bounded (TTL) so a crashed holder cannot wedge the pair
// forever, and renewable for long mirrors. Owner is advisory (diagnostics only).
public sealed class SyncRunLockRow
{
    public string PairId { get; set; } = "";
    public DateTimeOffset LockedUntil { get; set; }
    public string? Owner { get; set; }
}

public sealed class SyncStateRow
{
    // Surrogate key (UserId|DeviceId). The uniqueness guarantee is the composite
    // (UserId, DeviceId) index — the same physical device can now hold a state row under
    // each user it is paired with, which a DeviceId-only unique index wrongly forbade.
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public DateTimeOffset LastSyncUtc { get; set; }
    public int LastCreated { get; set; }
    public int LastUpdated { get; set; }
    public int LastDeleted { get; set; }
}
