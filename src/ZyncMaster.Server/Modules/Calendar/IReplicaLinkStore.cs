namespace ZyncMaster.Server;

// User-scoped persistence for replica links. Every operation is scoped to
// ICurrentUserAccessor.UserId by construction: cross-user reads return null/empty and
// cross-user updates are no-ops returning false (repo rule + mandatory cross-user test).
public interface IReplicaLinkStore
{
    Task<ReplicaLink> AddAsync(ReplicaLink link, CancellationToken ct = default);
    Task<ReplicaLink?> GetAsync(string id, CancellationToken ct = default);

    // All of the caller's links, every status. Callers filter; the volume is per-event (small).
    Task<IReadOnlyList<ReplicaLink>> ListAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ReplicaLink>> ListBySourceEventAsync(
        string sourceEventId, CancellationToken ct = default);

    // Persists MaskTitle, ContentHash, Status, DestinationEventId and UpdatedUtc of an owned
    // link. Returns false when the link does not exist or belongs to another user.
    Task<bool> UpdateAsync(ReplicaLink link, CancellationToken ct = default);
}
