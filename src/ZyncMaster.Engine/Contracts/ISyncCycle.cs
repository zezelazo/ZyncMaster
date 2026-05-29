using System.Threading;
using System.Threading.Tasks;

namespace ZyncMaster.Engine;

public interface ISyncCycle
{
    Task<SyncResult> RunCycleAsync(CancellationToken ct = default);
}
