namespace SyncMaster.Server;

public sealed record SyncState
{
    public required string DeviceId { get; init; }
    public DateTimeOffset LastSyncUtc { get; init; }
    public int LastCreated { get; init; }
    public int LastUpdated { get; init; }
    public int LastDeleted { get; init; }
}

public interface ISyncStateStore
{
    Task SetAsync(SyncState state, CancellationToken ct = default);
    Task<SyncState?> GetAsync(string deviceId, CancellationToken ct = default);
}
