namespace ZyncMaster.Server;

// Server-side mutual exclusion for a sync pair's mirror run (plan v2 §B-1). Acquisition is
// atomic and time-bounded; only one holder per PairId can run the mirror at a time. The
// lock is acquired INSIDE /api/pairs/{id}/push and /run (not from a client hint) so two
// executors — an App tick and a manual run, or two overlapping ticks — can never run the
// same destructive mirror concurrently.
public interface ISyncRunLock
{
    // Tries to acquire the lock for pairId until now+ttl. Returns a handle to release in a
    // finally (await using) on success, or null when another executor already holds it (the
    // endpoint then responds 409 without running the mirror). Atomic: implemented as
    // `UPDATE ... WHERE PairId=@id AND LockedUntil < @now` (rowsAffected==1) or an INSERT
    // when no row exists, so concurrent callers cannot both win.
    Task<ISyncRunLockHandle?> TryAcquireAsync(string pairId, TimeSpan ttl, string? owner = null, CancellationToken ct = default);
}

// Release-on-dispose handle for an acquired run lock. Renewable for a long-running mirror.
public interface ISyncRunLockHandle : IAsyncDisposable
{
    string PairId { get; }

    // Extends the lock to now+ttl while still held, so a mirror that outlives the original
    // TTL does not lose the lock to a competing executor mid-run. No-op after release.
    Task RenewAsync(TimeSpan ttl, CancellationToken ct = default);
}
