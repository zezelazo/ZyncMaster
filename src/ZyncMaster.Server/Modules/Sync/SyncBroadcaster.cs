using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace ZyncMaster.Server;

// Live-push hub for the Sync module, the missing counterpart to ClipboardBroadcaster. Until now the
// Sync module had NO push channel at all: a pair run (manual /push or /run, or a cron RunDue on the
// VPS, or a run on another of the user's machines) only persisted to the DB, and any other open
// session learned about it solely by re-opening the Calendar screen. The user explicitly wants sync
// status to refresh in real time, so a completed run / a change to the pair set fans out here.
//
// It reuses the clipboard presence + routing table (ClipboardConnectionRegistry): the WS the App
// already holds for clipboard is the user's single live channel keyed by (UserId, DeviceId), so a
// sync frame rides the same socket. Frames carry a distinct "type" ("pair-run" / "pairs-changed")
// that existing clients ignore when unrecognized, so this is additive and non-breaking.
//
// Two responsibilities, both best-effort and user-scoped:
//   - BroadcastPairRunAsync:     a recorded run completed -> push the new LastResult + LastRunUtc for
//                                that pair to the user's OTHER sessions (the origin device, which just
//                                ran it and already has the result, is excluded — same as clipboard).
//   - BroadcastPairsChangedAsync: the pair SET changed (create / delete / re-target) -> tell the
//                                user's OTHER sessions to reload /api/pairs.
//
// Like ClipboardBroadcaster, a failure to one socket never throws out of these methods, and a dead
// socket is dropped from the registry so it stops appearing in presence.
public sealed class SyncBroadcaster
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Ignore,
    };

    private readonly ClipboardConnectionRegistry _registry;

    public SyncBroadcaster(ClipboardConnectionRegistry registry) =>
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    // Push a completed pair run to the user's OTHER live sessions so an open Calendar/Sync screen
    // refreshes its last-run + result without re-opening the screen. The origin device id (the device
    // that pushed/ran the pair, empty for a cookie/identity-bearer human or for the cron context) is
    // excluded — it already has the result in hand. The frame mirrors the /api/pairs DTO shape for
    // these fields (lastResult is the MirrorResult, lastRunUtc the recorded timestamp), so a client can
    // patch its in-memory pair row directly off the frame.
    public async Task BroadcastPairRunAsync(
        string userId,
        string originDeviceId,
        string pairId,
        MirrorResult result,
        DateTimeOffset lastRunUtc,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentNullException.ThrowIfNull(pairId);
        ArgumentNullException.ThrowIfNull(result);

        var frame = new
        {
            type = "pair-run",
            pairId,
            lastResult = result,
            lastRunUtc,
        };

        var json = Serialize(frame);
        foreach (var conn in _registry.ForUserExcept(userId, originDeviceId))
            await SendBestEffortAsync(conn, json, ct).ConfigureAwait(false);
    }

    // Push a "the pair set changed" signal to the user's OTHER live sessions so they reload /api/pairs.
    // Used when a pair is created, deleted or re-targeted — cases where a single per-pair patch is not
    // enough (a row appears/disappears) so the cheapest correct client reaction is a full reload. The
    // origin (which already applied the change locally) is excluded. Best-effort, same as runs.
    public async Task BroadcastPairsChangedAsync(string userId, string originDeviceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(userId);

        var json = Serialize(new { type = "pairs-changed" });
        foreach (var conn in _registry.ForUserExcept(userId, originDeviceId))
            await SendBestEffortAsync(conn, json, ct).ConfigureAwait(false);
    }

    private static string Serialize<T>(T value) => JsonConvert.SerializeObject(value, JsonSettings);

    // Returns true if the frame went out; false if the socket was closed or threw. A throwing or
    // non-open socket is removed from the registry so it stops showing up in presence broadcasts.
    private async Task<bool> SendBestEffortAsync(ClipboardConnection conn, string json, CancellationToken ct)
    {
        try
        {
            if (conn.Socket.State != WebSocketState.Open)
            {
                _registry.Remove(conn.UserId, conn.DeviceId);
                return false;
            }

            var bytes = Encoding.UTF8.GetBytes(json);
            await conn.Socket.SendAsync(
                new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, ct)
                .ConfigureAwait(false);
            return true;
        }
        catch
        {
            _registry.Remove(conn.UserId, conn.DeviceId);
            return false;
        }
    }
}
