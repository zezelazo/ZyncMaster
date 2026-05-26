using System.Collections.Concurrent;

namespace SyncMaster.Server;

public sealed class InMemoryDeviceStore : IDeviceStore
{
    private readonly ConcurrentDictionary<string, Device> _devices = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingPairing> _pending = new(StringComparer.Ordinal);

    public Task<Device> AddAsync(Device device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        _devices[device.Id] = device;
        return Task.FromResult(device);
    }

    public Task<Device?> GetAsync(string deviceId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        _devices.TryGetValue(deviceId, out var device);
        return Task.FromResult(device);
    }

    public Task<IReadOnlyList<Device>> ListAsync(CancellationToken ct = default)
    {
        IReadOnlyList<Device> snapshot = _devices.Values.ToList();
        return Task.FromResult(snapshot);
    }

    public Task UpdateAsync(Device device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        _devices[device.Id] = device;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string deviceId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        _devices.TryRemove(deviceId, out _);
        return Task.CompletedTask;
    }

    public Task SavePendingAsync(PendingPairing pairing, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        _pending[pairing.PairingId] = pairing;
        return Task.CompletedTask;
    }

    public Task<PendingPairing?> GetPendingAsync(string pairingId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pairingId);
        _pending.TryGetValue(pairingId, out var pairing);
        return Task.FromResult(pairing);
    }

    public Task<PendingPairing?> GetPendingByCodeAsync(string code, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        var match = _pending.Values.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.Ordinal));
        return Task.FromResult(match);
    }

    public Task UpdatePendingAsync(PendingPairing pairing, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        _pending[pairing.PairingId] = pairing;
        return Task.CompletedTask;
    }

    public Task RemovePendingAsync(string pairingId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pairingId);
        _pending.TryRemove(pairingId, out _);
        return Task.CompletedTask;
    }
}
