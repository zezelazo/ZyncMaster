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

    // Raised when the server broadcasts the live online-device roster ({type:"presence",
    // onlineDeviceIds:[...]}) over the connection (on any device connect/disconnect). The argument is
    // the full set of currently-online device ids. Consumers (the devices view) prefer this live set
    // and fall back to the last-seen heuristic until the first presence frame arrives.
    event Action<IReadOnlyList<string>> PresenceChanged;

    // Raised when the live connection DROPS (not a clean shutdown). The last cached presence roster is
    // now stale — a device that was "online" may simply be in the reconnect window — so consumers must
    // DISCARD the cached roster and fall back to the last-seen heuristic until a fresh presence frame
    // arrives on the next connect. Without this reset a genuinely-online device shows offline during
    // the reconnect window (the non-null cache bypasses the fallback).
    event Action PresenceReset;

    // Raised when the server broadcasts a per-device clipboard settings change ({type:"settings",
    // deviceId, settings:{...}}) so the user's OTHER open windows update without a manual refresh. The
    // arguments are the affected device id and its new settings.
    event Action<string, ClipboardSettings> SettingsChanged;
}
