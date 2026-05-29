using System.Collections.Concurrent;

namespace ZyncMaster.Server;

public sealed class InMemorySyncPairStore : ISyncPairStore
{
    private readonly ConcurrentDictionary<string, SyncPair> _pairs = new(StringComparer.Ordinal);

    public Task<SyncPair> AddAsync(SyncPair pair, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pair);
        _pairs[pair.Id] = pair;
        return Task.FromResult(pair);
    }

    public Task<SyncPair?> GetAsync(string id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        _pairs.TryGetValue(id, out var pair);
        return Task.FromResult(pair);
    }

    public Task<IReadOnlyList<SyncPair>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SyncPair>>(_pairs.Values.ToList());

    public Task UpdateAsync(SyncPair pair, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pair);
        _pairs[pair.Id] = pair;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        _pairs.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SyncPair>> ListByDestinationAccountAsync(string accountRef, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(accountRef);
        var matches = _pairs.Values
            .Where(p => AccountMatches(p.Destination.AccountRef, accountRef))
            .ToList();
        return Task.FromResult<IReadOnlyList<SyncPair>>(matches);
    }

    public Task<IReadOnlyList<SyncPair>> ListBySourceAccountAsync(string accountRef, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(accountRef);
        var matches = _pairs.Values
            .Where(p => AccountMatches(p.Source.AccountRef, accountRef))
            .ToList();
        return Task.FromResult<IReadOnlyList<SyncPair>>(matches);
    }

    // A null / empty AccountRef on an endpoint normalizes to the "default" account, so
    // a query for "default" must also match endpoints that left the ref unset.
    private static bool AccountMatches(string? endpointRef, string accountRef)
    {
        var normalizedEndpoint = string.IsNullOrWhiteSpace(endpointRef) ? "default" : endpointRef;
        var normalizedQuery = string.IsNullOrWhiteSpace(accountRef) ? "default" : accountRef;
        return string.Equals(normalizedEndpoint, normalizedQuery, StringComparison.Ordinal);
    }
}
