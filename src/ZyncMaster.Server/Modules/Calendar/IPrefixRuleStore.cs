namespace ZyncMaster.Server;

// User-scoped persistence for prefix rules (+ their destination lists). Same scoping contract
// as IReplicaLinkStore: cross-user reads are null/empty, cross-user mutations no-op false.
public interface IPrefixRuleStore
{
    Task<PrefixRule> AddAsync(PrefixRule rule, CancellationToken ct = default);
    Task<PrefixRule?> GetAsync(string id, CancellationToken ct = default);

    // The caller's rules ordered by SortOrder (the collision order of spec §5).
    Task<IReadOnlyList<PrefixRule>> ListAsync(CancellationToken ct = default);

    // Replaces Prefix/MaskTitle/Enabled/SortOrder AND the whole destination list.
    Task<bool> UpdateAsync(PrefixRule rule, CancellationToken ct = default);

    Task<bool> RemoveAsync(string id, CancellationToken ct = default);
}
