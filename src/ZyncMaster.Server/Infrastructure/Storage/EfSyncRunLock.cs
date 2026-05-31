using Microsoft.EntityFrameworkCore;
using ZyncMaster.Server.Data;

namespace ZyncMaster.Server;

// EF-backed run lock (plan v2 §B-1). Acquisition is a single atomic statement guarded by
// rowsAffected, portable across SQLite and SQL Server:
//
//   1. UPDATE SyncRunLocks SET LockedUntil=@until, Owner=@owner
//      WHERE PairId=@id AND LockedUntil < @now;       -- steals an expired/free lock
//      if rowsAffected == 1 -> acquired.
//   2. else INSERT a fresh row;                        -- first-ever lock for this pair
//      a unique-key violation here means a concurrent caller inserted first -> not acquired.
//
// Because the UPDATE filters on `LockedUntil < @now` and the DB serializes writes to the
// single PairId row, two concurrent executors can never both see rowsAffected==1. Release
// sets LockedUntil to the epoch so the next run re-acquires immediately.
public sealed class EfSyncRunLock : ISyncRunLock
{
    private static readonly DateTimeOffset Expired = DateTimeOffset.UnixEpoch;

    private readonly IDbContextFactory<ZyncMasterDbContext> _factory;

    public EfSyncRunLock(IDbContextFactory<ZyncMasterDbContext> factory)
        => _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async Task<ISyncRunLockHandle?> TryAcquireAsync(
        string pairId, TimeSpan ttl, string? owner = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pairId)) throw new ArgumentException("pairId required.", nameof(pairId));

        var now   = DateTimeOffset.UtcNow;
        var until = now.Add(ttl);

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Step 1: atomically steal a free/expired lock. The WHERE clause is the gate.
        var updated = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE SyncRunLocks SET LockedUntil = {until}, Owner = {owner} WHERE PairId = {pairId} AND LockedUntil < {now}",
            ct).ConfigureAwait(false);

        if (updated == 1)
            return new Handle(_factory, pairId);

        // Step 2: no row was stolen — either the lock is live (held by someone else) or no
        // row exists yet. Try to create it; a unique-violation means a concurrent caller won.
        try
        {
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO SyncRunLocks (PairId, LockedUntil, Owner) VALUES ({pairId}, {until}, {owner})",
                ct).ConfigureAwait(false);

            if (inserted == 1)
                return new Handle(_factory, pairId);
        }
        catch (DbUpdateException)
        {
            // Concurrent INSERT won the PK race.
        }
        catch (Exception ex) when (IsUniqueViolation(ex))
        {
            // Provider-specific unique/PK violation (the row appeared between our UPDATE and
            // INSERT). Treat as "someone else holds it".
        }

        return null;
    }

    // A live lock held by another caller, or a PK collision on the INSERT, both mean the
    // pair is busy. We surface that as "not acquired" rather than throwing.
    private static bool IsUniqueViolation(Exception ex)
    {
        var t = ex.GetType().Name;
        var msg = ex.Message ?? "";
        return t.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)
            || t.Contains("SqlException", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("duplicate", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Handle : ISyncRunLockHandle
    {
        private readonly IDbContextFactory<ZyncMasterDbContext> _factory;
        private bool _released;

        public Handle(IDbContextFactory<ZyncMasterDbContext> factory, string pairId)
        {
            _factory = factory;
            PairId = pairId;
        }

        public string PairId { get; }

        public async ValueTask DisposeAsync()
        {
            if (_released) return;
            _released = true;

            // Release = push LockedUntil into the past so the next run re-acquires at once.
            // Best-effort: a failure here just means the lock waits out its TTL.
            try
            {
                await using var db = await _factory.CreateDbContextAsync().ConfigureAwait(false);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE SyncRunLocks SET LockedUntil = {Expired} WHERE PairId = {PairId}")
                    .ConfigureAwait(false);
            }
            catch
            {
                // Swallow: the TTL guarantees eventual release even if this fails.
            }
        }
    }
}
