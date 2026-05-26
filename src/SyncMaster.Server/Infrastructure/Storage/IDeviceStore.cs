namespace SyncMaster.Server;

public interface IDeviceStore
{
    Task<Device> AddAsync(Device device, CancellationToken ct = default);
    Task<Device?> GetAsync(string deviceId, CancellationToken ct = default);
    Task<IReadOnlyList<Device>> ListAsync(CancellationToken ct = default);
    Task UpdateAsync(Device device, CancellationToken ct = default);
    Task RemoveAsync(string deviceId, CancellationToken ct = default);

    Task SavePendingAsync(PendingPairing pairing, CancellationToken ct = default);
    Task<PendingPairing?> GetPendingAsync(string pairingId, CancellationToken ct = default);
    Task<PendingPairing?> GetPendingByCodeAsync(string code, CancellationToken ct = default);
    Task UpdatePendingAsync(PendingPairing pairing, CancellationToken ct = default);
    Task RemovePendingAsync(string pairingId, CancellationToken ct = default);
}
