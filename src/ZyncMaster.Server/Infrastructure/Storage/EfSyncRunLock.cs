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

        // FIX B — every acquire mints a fresh fencing token. The Handle remembers it and releases
        // ONLY the row that still carries this exact token, so a late Dispose by a holder whose
        // lock already expired and was stolen cannot free the new owner's lock.
        var fence = Guid.NewGuid().ToString("N");

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Step 1: atomically steal a free/expired lock. The WHERE clause is the gate. The steal
        // also overwrites FenceToken with ours, so the previous holder's fence no longer matches.
        var updated = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE SyncRunLocks SET LockedUntil = {until}, Owner = {owner}, FenceToken = {fence} WHERE PairId = {pairId} AND LockedUntil < {now}",
            ct).ConfigureAwait(false);

        if (updated == 1)
            return new Handle(_factory, pairId, fence);

        // Step 2: no row was stolen — either the lock is live (held by someone else) or no
        // row exists yet. Try to create it; a unique-violation means a concurrent caller won.
        try
        {
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO SyncRunLocks (PairId, LockedUntil, Owner, FenceToken) VALUES ({pairId}, {until}, {owner}, {fence})",
                ct).ConfigureAwait(false);

            if (inserted == 1)
                return new Handle(_factory, pairId, fence);
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
        private readonly string _fence;
        private bool _released;

        public Handle(IDbContextFactory<ZyncMasterDbContext> factory, string pairId, string fence)
        {
            _factory = factory;
            PairId = pairId;
            _fence = fence;
        }

        public string PairId { get; }

        // FIX 2 — extend LockedUntil to now+ttl, but ONLY where this row still carries OUR fence.
        // A single atomic conditional UPDATE guarded by rowsAffected, exactly like acquire/release.
        // rowsAffected==1 means we renewed (we still hold the lock); 0 means we lost it (expired and
        // stolen, or the row vanished) and the caller must stop. Errors surface as false (lost) so a
        // transport blip is treated conservatively rather than as a held lock.
        public async Task<bool> RenewAsync(TimeSpan ttl, CancellationToken ct = default)
        {
            if (_released)
                return false;

            try
            {
                var until = DateTimeOffset.UtcNow.Add(ttl);
                await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
                var affected = await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE SyncRunLocks SET LockedUntil = {until} WHERE PairId = {PairId} AND FenceToken = {_fence}",
                    ct).ConfigureAwait(false);
                return affected == 1;
            }
            catch
            {
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_released) return;
            _released = true;

            // FIX B — release = push LockedUntil into the past so the next run re-acquires at once,
            // but ONLY if this row is still OURS (FenceToken matches). If our lock had expired and
            // another executor stole it (writing its own fence), the WHERE clause matches nothing
            // and we leave the new owner's lock untouched — no double-mirror. Best-effort: a failure
            // here just means the lock waits out its TTL.
            try
            {
                await using var db = await _factory.CreateDbContextAsync().ConfigureAwait(false);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE SyncRunLocks SET LockedUntil = {Expired} WHERE PairId = {PairId} AND FenceToken = {_fence}")
                    .ConfigureAwait(false);
            }
            catch
            {
                // Swallow: the TTL guarantees eventual release even if this fails.
            }
        }
    }
}
