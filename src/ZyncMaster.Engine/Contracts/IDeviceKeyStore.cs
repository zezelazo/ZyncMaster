using System.Threading;
using System.Threading.Tasks;

namespace ZyncMaster.Engine;

public interface IDeviceKeyStore
{
    Task SaveAsync(string apiKey, CancellationToken ct);
    Task<string?> LoadAsync(CancellationToken ct);
    Task ClearAsync(CancellationToken ct);
}
