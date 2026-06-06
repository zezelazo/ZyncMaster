using Microsoft.EntityFrameworkCore;
using ZyncMaster.Server.Data;

namespace ZyncMaster.Server;

// EF-backed sync-pair store. The Endpoint / MirrorResult records are stored as camelCase
// JSON columns and mapped back to the domain SyncPair on read. Account-match queries for
// the unlink cascade run in memory after loading the user's pairs because the AccountRef
// lives inside the serialized endpoint JSON (and "unset" must normalize to "default").
public sealed class EfSyncPairStore : ISyncPairStore
{
    private const string DefaultAccount = "default";

    private readonly IDbContextFactory<ZyncMasterDbContext> _factory;
    private readonly ICurrentUserAccessor _currentUser;

    public EfSyncPairStore(IDbContextFactory<ZyncMasterDbContext> factory, ICurrentUserAccessor currentUser)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
    }

    public async Task<SyncPair> AddAsync(SyncPair pair, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pair);
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.SyncPairs.Add(ToRow(pair));
        await db.SaveChangesAsync(ct);
        return pair;
    }

    public async Task<SyncPair?> GetAsync(string id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.SyncPairs.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == _currentUser.UserId && p.Id == id, ct);
        return row is null ? null : ToDomain(row);
    }

    public async Task<IReadOnlyList<SyncPair>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.SyncPairs.AsNoTracking()
            .Where(p => p.UserId == _currentUser.UserId)
            .ToListAsync(ct);
        return rows.Select(ToDomain).ToList();
    }

    public async Task UpdateAsync(SyncPair pair, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pair);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.SyncPairs
            .FirstOrDefaultAsync(p => p.UserId == _currentUser.UserId && p.Id == pair.Id, ct);
        if (row is null)
            return;
        Apply(row, pair);
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(string id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.SyncPairs
            .Where(p => p.UserId == _currentUser.UserId && p.Id == id)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<SyncPair>> ListByDestinationAccountAsync(string accountRef, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(accountRef);
        var all = await ListAsync(ct);
        return all.Where(p => AccountMatches(p.Destination.AccountRef, accountRef)).ToList();
    }

    public async Task<IReadOnlyList<SyncPair>> ListBySourceAccountAsync(string accountRef, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(accountRef);
        var all = await ListAsync(ct);
        return all.Where(p => AccountMatches(p.Source.AccountRef, accountRef)).ToList();
    }

    private static bool AccountMatches(string? endpointRef, string accountRef)
    {
        var normalizedEndpoint = string.IsNullOrWhiteSpace(endpointRef) ? DefaultAccount : endpointRef;
        var normalizedQuery = string.IsNullOrWhiteSpace(accountRef) ? DefaultAccount : accountRef;
        return string.Equals(normalizedEndpoint, normalizedQuery, StringComparison.Ordinal);
    }

    private SyncPairRow ToRow(SyncPair pair)
    {
        var row = new SyncPairRow { Id = pair.Id, UserId = _currentUser.UserId };
        Apply(row, pair);
        return row;
    }

    private static void Apply(SyncPairRow row, SyncPair pair)
    {
        row.Name = pair.Name;
        row.SourceJson = PairJson.Serialize(pair.Source);
        row.DestinationJson = PairJson.Serialize(pair.Destination);
        row.IntervalMin = pair.IntervalMin;
        row.State = pair.State;
        row.LastRunUtc = pair.LastRunUtc;
        row.LastResultJson = pair.LastResult is null ? null : PairJson.Serialize(pair.LastResult);
        // Persist the pending-cleanup list only when non-empty; an empty list stores null so the
        // common no-drain case leaves the column null (matching pre-FIX-3 rows).
        row.PendingCleanupJson = pair.PendingCleanupDestinations is { Count: > 0 } pend
            ? PairJson.Serialize(pend)
            : null;
        // COM device-pinning (Track B). Both flow as top-level columns; a normalize-to-null keeps a
        // blank PinnedDeviceId from masquerading as a real pin.
        row.PinnedDeviceId = string.IsNullOrWhiteSpace(pair.PinnedDeviceId) ? null : pair.PinnedDeviceId;
        row.SyncRequestedUtc = pair.SyncRequestedUtc;
    }

    private static SyncPair ToDomain(SyncPairRow r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        Source = PairJson.Deserialize<Endpoint>(r.SourceJson),
        Destination = PairJson.Deserialize<Endpoint>(r.DestinationJson),
        IntervalMin = r.IntervalMin,
        State = r.State,
        LastRunUtc = r.LastRunUtc,
        LastResult = r.LastResultJson is null ? null : PairJson.Deserialize<MirrorResult>(r.LastResultJson),
        PendingCleanupDestinations = string.IsNullOrEmpty(r.PendingCleanupJson)
            ? new List<Endpoint>()
            : PairJson.Deserialize<List<Endpoint>>(r.PendingCleanupJson),
        PinnedDeviceId = r.PinnedDeviceId,
        SyncRequestedUtc = r.SyncRequestedUtc,
    };
}
