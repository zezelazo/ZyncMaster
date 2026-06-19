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

    // Lazy-blob transfer for File items: upload the raw bytes to the blob store keyed by the item id,
    // and download them on demand. Kept separate from PublishAsync so the item frame stays metadata-only
    // and the bytes move only when a device actually pastes. DownloadBlobAsync returns null when the
    // blob is absent (not yet uploaded, or evicted by retention) so the caller can show "loading"/retry.
    Task UploadBlobAsync(string id, byte[] content, CancellationToken ct = default);
    Task<byte[]?> DownloadBlobAsync(string id, CancellationToken ct = default);

    // Removes one history entry (DELETE /api/clipboard/items/{id}). User-scoped server-side, so an
    // unknown/foreign id is a silent no-op. The server fans a "deleted" frame out to the user's OTHER
    // devices; this caller already removed it locally, so it does not get its own deletion echoed back.
    Task DeleteEntryAsync(string id, CancellationToken ct = default);
    Task<ClipboardSettings> GetSettingsAsync(string deviceId, CancellationToken ct = default);
    Task UpdateSettingsAsync(string deviceId, ClipboardSettings s, CancellationToken ct = default);

    // The user's device roster as the clipboard module sees it (GET /api/clipboard/devices): id +
    // name, the live online flag, and the key-admission fields (needsTextKey + publicKeyBase64). A
    // key-holder reads this to find which peers are waiting for the E2E text key.
    Task<IReadOnlyList<ClipboardDeviceKeyInfo>> GetDevicesAsync(CancellationToken ct = default);
    Task<bool> RelayKeyAsync(string fromDeviceId, string targetDeviceId, byte[] wrappedKey, CancellationToken ct = default);
    Task ConnectAsync(CancellationToken ct = default);
    event Action<ClipboardEntry> ItemReceived;
    event Action<string, byte[]> KeyReceived;

    // Raised when the server broadcasts a history deletion ({type:"deleted", id}) over the live
    // connection — another of the user's devices (or the human panel) removed an entry. Consumers drop
    // the item with that id from any open list (dashboard clipboard screen, floating viewer). The
    // argument is the deleted item id.
    event Action<string> DeletedReceived;

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

    // Raised when the server broadcasts a completed Sync pair run ({type:"pair-run", pairId,
    // lastResult:{...}, lastRunUtc}) over the SAME live connection (the Sync module reuses the
    // clipboard socket — see SyncBroadcaster). The server already excludes the device that ran the
    // pair, so this fires only for the user's OTHER sessions: an open Calendar/Sync screen patches
    // that pair's last-run + result row live without re-opening the screen. The arguments are the
    // pair id, the MirrorResult serialized as a raw JSON object string (counts the UI maps directly),
    // and the recorded run timestamp (round-trip ISO string, may be empty if the server omitted it).
    event Action<string, string, string> PairRunReceived;

    // Raised when the server broadcasts that the user's pair SET changed ({type:"pairs-changed"}) —
    // a pair was created, deleted or re-targeted on another session. A single per-row patch is not
    // enough (a row appears/disappears), so consumers reload the pair list. No payload.
    event Action PairsChanged;
}
