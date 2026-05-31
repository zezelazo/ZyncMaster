using System.Threading;
using System.Threading.Tasks;

namespace ZyncMaster.App.Bridge;

// The identity access + refresh token pair the App holds for the signed-in user. The access
// token is short-lived (renewed via the refresh token); the refresh token is long-lived and
// the more sensitive of the two (it can mint fresh access tokens), so the whole pair is stored
// encrypted at rest (DPAPI on Windows).
public sealed record IdentityTokens(string AccessToken, string RefreshToken);

// Persists the identity token pair across App restarts. The implementation encrypts at rest;
// LoadAsync returns null when nothing is stored (signed out). Mirrors IDeviceKeyStore.
public interface IIdentityTokenCache
{
    Task SaveAsync(IdentityTokens tokens, CancellationToken ct = default);
    Task<IdentityTokens?> LoadAsync(CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}
