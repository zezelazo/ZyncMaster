namespace ZyncMaster.Server;

public interface ISyncPairStore
{
    Task<SyncPair> AddAsync(SyncPair pair, CancellationToken ct = default);
    Task<SyncPair?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<SyncPair>> ListAsync(CancellationToken ct = default);
    Task UpdateAsync(SyncPair pair, CancellationToken ct = default);
    Task RemoveAsync(string id, CancellationToken ct = default);

    // Pairs whose Destination / Source references the given account. Used by the
    // account-unlink cascade to disable affected pairs.
    Task<IReadOnlyList<SyncPair>> ListByDestinationAccountAsync(string accountRef, CancellationToken ct = default);
    Task<IReadOnlyList<SyncPair>> ListBySourceAccountAsync(string accountRef, CancellationToken ct = default);
}
