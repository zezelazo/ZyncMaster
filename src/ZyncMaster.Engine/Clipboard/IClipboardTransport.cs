using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ZyncMaster.Engine;

// Device-side channel to the server clipboard module: publishes encrypted entries, reads history,
// manages per-device settings, relays wrapped keys, and pushes inbound items/keys over the live
// connection.
public interface IClipboardTransport
{
    Task PublishAsync(ClipboardEntry encrypted, CancellationToken ct = default);
    Task<IReadOnlyList<ClipboardEntry>> GetHistoryAsync(CancellationToken ct = default);
    Task<ClipboardSettings> GetSettingsAsync(string deviceId, CancellationToken ct = default);
    Task UpdateSettingsAsync(string deviceId, ClipboardSettings s, CancellationToken ct = default);
    Task<bool> RelayKeyAsync(string fromDeviceId, string targetDeviceId, byte[] wrappedKey, CancellationToken ct = default);
    Task ConnectAsync(CancellationToken ct = default);
    event Action<ClipboardEntry> ItemReceived;
    event Action<string, byte[]> KeyReceived;
}
