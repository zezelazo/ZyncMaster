using ZyncMaster.Server.Data;

namespace ZyncMaster.Server;

// A freshly issued identity access token: the protected blob the App carries plus the
// metadata (jti / expiry) the caller needs to track or revoke it later.
public sealed record IdentityToken(string Token, string Jti, DateTimeOffset ExpiresAt);

// The validated identity behind an access token. Returned by ValidateAccessToken when the
// blob is intact, unexpired, and the jti has not been revoked.
public sealed record IdentityPrincipal(
    string Jti,
    string UserId,
    string Email,
    string DisplayName,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

// Issues, validates, and revokes the internal Server<->App identity tokens (plan v2 §A-1).
//
// - Access tokens are short-lived, protected with IDataProtectionProvider (a serialized JSON
//   payload, NOT a standard JWT), and registered by jti so they can be revoked early.
// - Refresh tokens are long-lived, opaque (random), stored only as a hash, and redeemed for
//   a new access token.
//
// SECURITY: this service is the trust boundary for the identity bearer. A 30-day token with
// no revocation would be a master key; hence jti + revocation ledger + short TTL + refresh.
public interface IIdentityTokenService
{
    // Mints a new access token for the user and registers its jti. Pure-CPU (protect +
    // ledger write happen together); kept synchronous to mirror the JWT-style call site.
    IdentityToken IssueAccessToken(UserRow user);

    // Returns the principal when the token is intact, unexpired, and its jti is live;
    // null on any tamper / expiry / revocation / unknown-jti.
    IdentityPrincipal? ValidateAccessToken(string token);

    // Mints a long-lived opaque refresh token for the user, persisting only its hash.
    // Returns the clear token value (shown once to the caller, never stored).
    Task<string> IssueRefreshTokenAsync(string userId, CancellationToken ct = default);

    // Validates the presented refresh token (hash match + unexpired + not revoked) and
    // returns its owning user so the caller can issue a fresh access token; null otherwise.
    Task<UserRow?> RedeemRefreshTokenAsync(string refreshToken, CancellationToken ct = default);

    // Revokes a single access token by jti (e.g. a targeted session kill).
    Task RevokeAccessAsync(string jti, CancellationToken ct = default);

    // Revokes ALL of a user's live access AND refresh tokens ("sign out everywhere").
    Task RevokeAllForUserAsync(string userId, CancellationToken ct = default);
}
