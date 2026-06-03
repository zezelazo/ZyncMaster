using System.Threading;
using System.Threading.Tasks;

namespace ZyncMaster.Engine;

// Supplies the signed-in user's IDENTITY access token (the IdentityBearer) for the human-only
// management surface (accounts + pairs). The App backs this with its DPAPI-encrypted identity
// token cache; LoadAsync returns null when the user is signed out. Kept as an Engine-level port
// so the Engine (e.g. PairScheduler) can reach the bearer-gated pairs listing without depending
// on the App project.
public interface IIdentityTokenProvider
{
    Task<string?> LoadAccessTokenAsync(CancellationToken ct = default);
}
