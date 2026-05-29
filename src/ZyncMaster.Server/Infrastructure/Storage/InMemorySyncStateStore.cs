using System.Collections.Concurrent;

namespace ZyncMaster.Server;

public sealed class InMemorySyncStateStore : ISyncStateStore
{
    private readonly ConcurrentDictionary<string, SyncState> _states = new(StringComparer.Ordinal);

    public Task SetAsync(SyncState state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        _states[state.DeviceId] = state;
        return Task.CompletedTask;
    }

    public Task<SyncState?> GetAsync(string deviceId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        _states.TryGetValue(deviceId, out var state);
        return Task.FromResult(state);
    }
}
