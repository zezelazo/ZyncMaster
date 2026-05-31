using System.Threading;
using System.Threading.Tasks;

namespace ZyncMaster.App.Bridge;

// The identity profile returned by GET /api/identity/me. A thin DTO the login service maps into
// an IdentityState alongside the locally-tracked access-token expiry.
public sealed record IdentityProfile(string? UserId, string? Email, string? DisplayName, string? Plan);

// The token pair returned by POST /identity/refresh: a freshly minted access token plus the
// rotated refresh token (the old one is now dead server-side).
public sealed record RefreshResult(string AccessToken, string NewRefreshToken);

// The App-side HTTP surface against the Server's identity endpoints. Abstracted so the
// IdentityLoginService can be unit-tested with a fake (the real impl is a thin HttpClient
// wrapper, untested like the other infrastructure clients per CLAUDE.md).
public interface IIdentityServerClient
{
    // POST /identity/handle/redeem {handle} → {accessToken, refreshToken}. Returns null when the
    // handle is unknown/expired/already-consumed (the Server replies 410).
    Task<IdentityTokens?> RedeemHandleAsync(string handle, CancellationToken ct = default);

    // POST /identity/refresh {refreshToken} → {accessToken, newRefreshToken}. Returns null when
    // the token is invalid/expired/revoked (the Server replies 401).
    Task<RefreshResult?> RefreshAsync(string refreshToken, CancellationToken ct = default);

    // GET /api/identity/me with the access token as Bearer. Returns null on 401 (token rejected).
    Task<IdentityProfile?> GetMeAsync(string accessToken, CancellationToken ct = default);

    // POST /identity/magic-link {email, port, nonce}. Returns true on the constant 202 the Server
    // sends whether or not the email maps to a user; false only on a transport/structural failure.
    Task<bool> RequestMagicLinkAsync(string email, int port, string nonce, CancellationToken ct = default);
}
