namespace ZyncMaster.Server.Data;

// A single external-identity login (one provider's view of a user). Multiple logins can
// point at the same canonical UserRow when they have been linked (same verified email).
// Kept in a separate file so the new identity schema doesn't disturb Entities.cs / the
// existing migrations history.
public sealed class IdentityLoginRow
{
    public string Id { get; set; } = "";

    // FK -> Users.Id (the canonical user this login resolves to).
    public string UserId { get; set; } = "";

    // "local" | "microsoft" | "google" | "facebook".
    public string Provider { get; set; } = "";

    // The provider's stable subject identifier for the user (oid / sub / etc.).
    public string ProviderSubject { get; set; } = "";

    public string Email { get; set; } = "";

    public bool EmailVerified { get; set; }

    public DateTimeOffset LinkedAt { get; set; }
}

// One issued identity access token, registered by its jti so it can be revoked before its
// natural expiry. The token blob itself is NOT stored (it lives only with the App); this row
// is the revocation ledger consulted on every ValidateAccessToken. Expired rows may be purged.
public sealed class IdentityAccessTokenRow
{
    // The unique token id (jti) carried inside the protected blob. Primary key.
    public string Jti { get; set; } = "";

    // FK -> Users.Id (the user the token authenticates as).
    public string UserId { get; set; } = "";

    public DateTimeOffset IssuedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    // Set when the token was explicitly revoked (logout / revoke-all). Null while live.
    public DateTimeOffset? RevokedAt { get; set; }
}

// One long-lived identity refresh token. The opaque token value is never stored in the clear:
// only its SHA-256 hash is persisted, so a database leak cannot recover live refresh tokens.
// Redeemed by hashing the presented value and matching against TokenHash.
public sealed class IdentityRefreshTokenRow
{
    // Surrogate id (random GUID). Primary key. NOT the token value.
    public string Id { get; set; } = "";

    // FK -> Users.Id (the user this refresh token belongs to).
    public string UserId { get; set; } = "";

    // Base64url-encoded SHA-256 of the opaque refresh token value.
    public string TokenHash { get; set; } = "";

    public DateTimeOffset IssuedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    // Set when revoked (logout-all). Null while live.
    public DateTimeOffset? RevokedAt { get; set; }
}
