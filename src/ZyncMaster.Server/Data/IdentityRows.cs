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
// is the revocation ledger consulted on every ValidateAccessToken.
//
// Purge policy (when implemented): rows may ONLY be deleted when ExpiresAt <= now. NEVER purge
// by any other column — a revoked-but-unexpired row must survive so revocation is enforced.
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

// One outstanding magic-link login challenge. Clicking the link proves possession of the email,
// so the resulting local login is treated as emailVerified:true (unlike the Microsoft flow). The
// token value is never stored in the clear — only its base64url SHA-256 hash — so a database leak
// cannot reconstruct a live link. Port/Nonce are carried so the callback can complete the same
// loopback redirect the App's /connect-style flow expects (the App generated them in the POST).
//
// Ephemeral by design (plan deferred §4 / C-7): rows are short-lived (TTL minutes) and may be
// purged once ExpiresAt has passed OR ConsumedAt is set; nothing else depends on them surviving.
public sealed class MagicLinkRow
{
    // Surrogate id (random GUID). Primary key. NOT the token value.
    public string Id { get; set; } = "";

    // Base64url-encoded SHA-256 of the opaque 32-byte token. Unique: the token IS the lookup key
    // at callback time, hashed before the query.
    public string TokenHash { get; set; } = "";

    // Normalized recipient email (Trim().ToLowerInvariant()). Indexed for the per-email rate-limit
    // window count.
    public string Email { get; set; } = "";

    // Loopback port the App is listening on, echoed back in the callback redirect.
    public int Port { get; set; }

    // App-generated nonce, echoed back so the App can tie the callback to its request.
    public string Nonce { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    // Set inside the single-use transaction at callback time. Null while the link is unused; a
    // non-null value means the link was already consumed and a second click must fail.
    public DateTimeOffset? ConsumedAt { get; set; }
}
