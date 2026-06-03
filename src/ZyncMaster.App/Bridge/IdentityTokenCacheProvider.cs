using System.Threading;
using System.Threading.Tasks;
using ZyncMaster.Engine;

namespace ZyncMaster.App.Bridge;

// Adapts the App's DPAPI-encrypted identity token cache to the Engine's IIdentityTokenProvider
// port, so Engine components (e.g. PairScheduler) can present the signed-in user's identity bearer
// on the human-only pairs surface without the Engine project depending on the App. Returns null
// when signed out (no tokens cached).
public sealed class IdentityTokenCacheProvider : IIdentityTokenProvider
{
    private readonly IIdentityTokenCache _cache;

    public IdentityTokenCacheProvider(IIdentityTokenCache cache)
    {
        _cache = cache ?? throw new System.ArgumentNullException(nameof(cache));
    }

    public async Task<string?> LoadAccessTokenAsync(CancellationToken ct = default)
    {
        var tokens = await _cache.LoadAsync(ct);
        return tokens?.AccessToken;
    }
}
