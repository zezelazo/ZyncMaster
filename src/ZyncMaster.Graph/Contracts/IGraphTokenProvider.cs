using System.Threading;
using System.Threading.Tasks;

namespace ZyncMaster.Graph;

public interface IGraphTokenProvider
{
    // Acquires an access token for Microsoft Graph. Performs an interactive
    // browser sign-in on first run, then refreshes silently from a DPAPI-protected cache.
    //
    // Pass forceRefresh: true after receiving a 401 from a downstream call. The silent
    // path returns a cached bearer until its in-memory expiry; if Graph rejects that
    // token (revoked, conditional-access policy change, clock skew) re-asking with
    // the default value yields the same expired bearer and the 401 retry becomes a no-op.
    // forceRefresh bypasses the bearer cache by setting WithForceRefresh(true) on MSAL.
    Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
}
