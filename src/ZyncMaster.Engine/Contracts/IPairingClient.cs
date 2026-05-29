using System.Threading;
using System.Threading.Tasks;

namespace ZyncMaster.Engine;

public interface IPairingClient
{
    Task<PairStart> StartAsync(string deviceName, CancellationToken ct);
    Task<PairComplete> CompleteAsync(string pairingId, CancellationToken ct);
}
