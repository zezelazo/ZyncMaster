using System.Collections.Concurrent;

namespace ZyncMaster.Server;

public sealed class InMemoryDeviceStore : IDeviceStore
{
    private readonly ConcurrentDictionary<string, Device> _devices = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingPairing> _pending = new(StringComparer.Ordinal);

    // Serialises the read-modify-write of the idempotent approve so the in-memory store mirrors the
    // EF conditional UPDATE's atomicity (two concurrent approves of the same code: exactly one wins).
    private readonly object _pendingApproveGate = new();

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

    public Task<PendingPairing?> GetPendingByCodeAsync(
        string code, DateTimeOffset notBefore, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        var match = _pending.Values.FirstOrDefault(p =>
            string.Equals(p.Code, code, StringComparison.Ordinal) && p.CreatedUtc >= notBefore);
        return Task.FromResult(match);
    }

    public Task<bool> TryMarkApprovedAsync(
        string code, DateTimeOffset notBefore, string approvedDeviceId, string oneTimeApiKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(approvedDeviceId);
        ArgumentNullException.ThrowIfNull(oneTimeApiKey);

        // Atomic, idempotent claim mirroring the EF conditional UPDATE. The lock serialises two
        // concurrent approves of the same code so exactly one observes the row still unapproved and
        // flips it; the other sees Approved == true and returns false (no phantom device, no key
        // overwrite). Expired (CreatedUtc < notBefore) and unknown codes return false.
        lock (_pendingApproveGate)
        {
            var match = _pending.Values.FirstOrDefault(p =>
                string.Equals(p.Code, code, StringComparison.Ordinal)
                && !p.Approved
                && p.CreatedUtc >= notBefore);
            if (match is null)
                return Task.FromResult(false);

            _pending[match.PairingId] = match with
            {
                Approved = true,
                ApprovedDeviceId = approvedDeviceId,
                OneTimeApiKey = oneTimeApiKey,
            };
            return Task.FromResult(true);
        }
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

    public Task<int> PurgeExpiredPendingAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        var expired = _pending.Values.Where(p => p.CreatedUtc < cutoff).Select(p => p.PairingId).ToList();
        var removed = 0;
        foreach (var id in expired)
        {
            if (_pending.TryRemove(id, out _))
                removed++;
        }
        return Task.FromResult(removed);
    }
}
