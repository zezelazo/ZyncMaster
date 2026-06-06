namespace ZyncMaster.Server;

// Server-side mutual exclusion for a sync pair's mirror run (plan v2 §B-1). Acquisition is
// atomic and time-bounded; only one holder per PairId can run the mirror at a time. The
// lock is acquired INSIDE /api/pairs/{id}/push and /run (not from a client hint) so two
// executors — an App tick and a manual run, or two overlapping ticks — can never run the
// same destructive mirror concurrently.
//
// TTL contract: the lock auto-expires after ServerOptions.SyncRunLockTtlMinutes so a crashed
// executor cannot wedge a pair forever.
//
// FIX 2 (run-lock renewal) — the lock is now RENEWED mid-run: the executor periodically calls
// handle.RenewAsync (driven by SyncRunLockHeartbeat) to push LockedUntil forward while the mirror
// is still working. This removes the previous hazard where a long mirror (many calendars, paging,
// retries) could outlive a fixed TTL, let the lock expire WHILE RUNNING, and allow a second
// executor to start a concurrent destructive sweep against the same calendar. With renewal the TTL
// only needs to exceed one heartbeat interval, not the whole worst-case mirror duration; if the
// executor crashes, renewal stops and the lock expires after at most one TTL so the pair is never
// wedged.
public interface ISyncRunLock
{
    // Tries to acquire the lock for pairId until now+ttl. Returns a handle to release in a
    // finally (await using) on success, or null when another executor already holds it (the
    // endpoint then responds 409 without running the mirror). Atomic: implemented as
    // `UPDATE ... WHERE PairId=@id AND LockedUntil < @now` (rowsAffected==1) or an INSERT
    // when no row exists, so concurrent callers cannot both win.
    Task<ISyncRunLockHandle?> TryAcquireAsync(string pairId, TimeSpan ttl, string? owner = null, CancellationToken ct = default);
}

// Acquired run-lock handle. Released on dispose, and RENEWED mid-run via RenewAsync (FIX 2).
public interface ISyncRunLockHandle : IAsyncDisposable
{
    string PairId { get; }

    // FIX 2 — extend this lock's expiry to now+ttl, but ONLY while this handle still owns the row
    // (its fence token still matches). Returns true when the renewal landed (we still hold the
    // lock) and false when it did not (the lock had already expired and been stolen, or the row is
    // gone) — a false result tells the executor it has lost the lock and should treat the run as
    // unsafe. Best-effort and idempotent; safe to call repeatedly from a heartbeat loop.
    Task<bool> RenewAsync(TimeSpan ttl, CancellationToken ct = default);
}
