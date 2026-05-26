using System.Threading;
using System.Threading.Tasks;
using SyncMaster.App.State;

namespace SyncMaster.App.Bridge;

// The set of operations the web UI can invoke on the host. UiBridge dispatches each
// inbound action to one of these; EngineActions implements them over the real sync
// engine. Kept narrow on purpose: every action maps to exactly one web verb.
public interface IEngineActions
{
    Task<AppStatus> GetStatusAsync(CancellationToken ct = default);
    Task<SyncMaster.Engine.SyncResult> SyncNowAsync(CancellationToken ct = default);
    Task<SyncMaster.Engine.PairingOutcome> PairAsync(CancellationToken ct = default);
    Task SaveConfigAsync(string configJson, CancellationToken ct = default);
    Task SetPausedAsync(bool paused, CancellationToken ct = default);
}
