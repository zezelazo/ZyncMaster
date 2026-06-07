using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ZyncMaster.Engine;

// Persists the device's E2E key material: the shared symmetric text key (wrapped at rest by the
// platform store) and the per-device RSA keypair used to relay that key to a new device.
public interface IClipboardKeyStore
{
    Task<byte[]?> LoadTextKeyAsync(CancellationToken ct = default);
    Task SaveTextKeyAsync(byte[] key, CancellationToken ct = default);
    Task<(byte[] publicKey, RSA privateKey)> EnsureDeviceKeypairAsync(CancellationToken ct = default);
}
