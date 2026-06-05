namespace ZyncMaster.Server;

public interface IDeviceStore
{
    Task<Device> AddAsync(Device device, CancellationToken ct = default);
    Task<Device?> GetAsync(string deviceId, CancellationToken ct = default);
    Task<IReadOnlyList<Device>> ListAsync(CancellationToken ct = default);
    Task UpdateAsync(Device device, CancellationToken ct = default);
    Task RemoveAsync(string deviceId, CancellationToken ct = default);

    Task SavePendingAsync(PendingPairing pairing, CancellationToken ct = default);
    Task<PendingPairing?> GetPendingAsync(string pairingId, CancellationToken ct = default);

    // Resolves a pending pairing by its (random, short-lived) code, but ONLY when it has not yet
    // expired: rows whose CreatedUtc is strictly before `notBefore` are treated as gone (FIX A — an
    // expired code must neither be viewable at /pair nor approvable). Callers pass
    // now - PendingPairingTtlMinutes as the cutoff.
    Task<PendingPairing?> GetPendingByCodeAsync(
        string code, DateTimeOffset notBefore, CancellationToken ct = default);

    // Atomically claims a pending pairing for approval (FIX A — idempotent approve). Performs a
    // single conditional update that sets Approved + ApprovedDeviceId + OneTimeApiKey ONLY when the
    // row matches the code, is still UNAPPROVED, and has not expired (CreatedUtc >= notBefore).
    // Returns true iff THIS call won the claim (exactly one row updated). A second concurrent or
    // sequential approve of the same code returns false and changes nothing, so a phantom device is
    // never created and the live OneTimeApiKey is never overwritten. Expired/unknown codes return
    // false too.
    Task<bool> TryMarkApprovedAsync(
        string code, DateTimeOffset notBefore, string approvedDeviceId, string oneTimeApiKey,
        CancellationToken ct = default);

    Task UpdatePendingAsync(PendingPairing pairing, CancellationToken ct = default);
    Task RemovePendingAsync(string pairingId, CancellationToken ct = default);

    // Deletes every pending pairing whose CreatedUtc is strictly before `cutoff` (FIX A — purge of
    // expired pairings). Returns the number of rows removed. Set-based on the EF store.
    Task<int> PurgeExpiredPendingAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
