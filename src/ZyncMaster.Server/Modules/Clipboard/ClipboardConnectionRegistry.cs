using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace ZyncMaster.Server;

// One live clipboard WebSocket for a given (UserId, DeviceId). The registry holds the open
// socket so the hub can fan a published item out to the user's other devices and route a
// relayed key to a specific target device.
public sealed class ClipboardConnection
{
    public required string UserId { get; init; }
    public required string DeviceId { get; init; }
    public required WebSocket Socket { get; init; }
}

// In-memory presence + routing table for clipboard WebSockets, keyed by user then device.
// Singleton, thread-safe (ConcurrentDictionary). A reconnecting device replaces its prior
// connection (AddOrUpdate on DeviceId). Everything is process-local — presence is not
// persisted and does not survive a restart, which is the intended F1a behaviour.
public sealed class ClipboardConnectionRegistry
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ClipboardConnection>> _byUser = new();

    public void Add(ClipboardConnection c) =>
        _byUser.GetOrAdd(c.UserId, _ => new()).AddOrUpdate(c.DeviceId, c, (_, __) => c);

    public void Remove(string userId, string deviceId)
    {
        if (_byUser.TryGetValue(userId, out var devs)) devs.TryRemove(deviceId, out _);
    }

    public IReadOnlyList<ClipboardConnection> ForUserExcept(string userId, string exceptDeviceId) =>
        _byUser.TryGetValue(userId, out var devs)
            ? devs.Values.Where(c => c.DeviceId != exceptDeviceId).ToList()
            : Array.Empty<ClipboardConnection>();

    public ClipboardConnection? Find(string userId, string deviceId) =>
        _byUser.TryGetValue(userId, out var devs) && devs.TryGetValue(deviceId, out var c) ? c : null;

    public IReadOnlyCollection<string> OnlineDeviceIds(string userId) =>
        _byUser.TryGetValue(userId, out var devs) ? devs.Keys.ToList() : Array.Empty<string>();
}
