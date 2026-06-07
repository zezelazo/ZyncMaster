using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace ZyncMaster.Server;

// Fan-out hub for the clipboard WebSocket protocol. Three responsibilities, all best-effort:
//   - BroadcastItemAsync: push a newly published item to the user's OTHER devices (not origin).
//   - RelayKeyAsync:      forward a wrapped E2E key to one specific target device, if online.
//   - BroadcastPresenceAsync: tell all of the user's devices the current online set.
//
// The server treats item payloads and wrapped keys as OPAQUE bytes — for Text the payload is
// ciphertext it cannot read, and the wrapped key is never persisted or logged. Sends are
// best-effort: a failure to one socket never throws out of these methods, and a dead socket is
// dropped from the registry so it stops appearing in presence.
public sealed class ClipboardBroadcaster
{
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Ignore,
    };

    private readonly ClipboardConnectionRegistry _registry;

    public ClipboardBroadcaster(ClipboardConnectionRegistry registry) =>
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public async Task BroadcastItemAsync(ClipboardItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);

        var frame = new
        {
            type = "item",
            item = new
            {
                id = item.Id,
                type = item.Type.ToString(),
                originDeviceId = item.OriginDeviceId,
                originDeviceName = item.OriginDeviceName,
                createdUtc = item.CreatedUtc,
                sizeBytes = item.SizeBytes,
                payloadBase64 = Convert.ToBase64String(item.Payload),
                thumbnailBase64 = item.Thumbnail is { } thumb ? Convert.ToBase64String(thumb) : null,
                preview = item.Preview,
            },
        };

        var json = Serialize(frame);
        foreach (var conn in _registry.ForUserExcept(item.UserId, item.OriginDeviceId))
            await SendBestEffortAsync(conn, json, ct).ConfigureAwait(false);
    }

    public async Task<bool> RelayKeyAsync(string userId, WrappedKeyEnvelope env, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(env);

        var target = _registry.Find(userId, env.TargetDeviceId);
        if (target is null) return false;

        // The wrapped key is opaque and transient: serialized straight to the target socket,
        // never written to storage and never logged.
        var json = Serialize(new
        {
            type = "key",
            fromDeviceId = env.FromDeviceId,
            wrappedKeyBase64 = Convert.ToBase64String(env.WrappedKey),
        });

        return await SendBestEffortAsync(target, json, ct).ConfigureAwait(false);
    }

    public async Task BroadcastPresenceAsync(string userId, CancellationToken ct)
    {
        var ids = _registry.OnlineDeviceIds(userId);
        var json = Serialize(new { type = "presence", onlineDeviceIds = ids });

        // Snapshot of all the user's connections (ForUserExcept with a non-matching device id).
        foreach (var conn in _registry.ForUserExcept(userId, exceptDeviceId: string.Empty))
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
