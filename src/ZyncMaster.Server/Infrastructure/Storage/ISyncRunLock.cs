namespace ZyncMaster.Server;

// Server-side mutual exclusion for a sync pair's mirror run (plan v2 §B-1). Acquisition is
// atomic and time-bounded; only one holder per PairId can run the mirror at a time. The
// lock is acquired INSIDE /api/pairs/{id}/push and /run (not from a client hint) so two
// executors — an App tick and a manual run, or two overlapping ticks — can never run the
// same destructive mirror concurrently.
//
// TTL contract: the lock auto-expires after ServerOptions.SyncRunLockTtlMinutes so a crashed
// executor cannot wedge a pair forever. There is NO mid-run renewal — the lock is held for a
// single acquire/dispose around the whole read+mirror — so the TTL MUST exceed the worst-case
// duration of one mirror. If a mirror ever outlives the TTL, the lock expires while still
// running and a second executor could acquire and run a concurrent destructive sweep against
// the same calendar. The default 8 min against a 14-day / $top=50 window is comfortably above
// the observed mirror cost; if the window or page size grows materially, re-check this margin.
public interface ISyncRunLock
{
    // Tries to acquire the lock for pairId until now+ttl. Returns a handle to release in a
    // finally (await using) on success, or null when another executor already holds it (the
    // endpoint then responds 409 without running the mirror). Atomic: implemented as
    // `UPDATE ... WHERE PairId=@id AND LockedUntil < @now` (rowsAffected==1) or an INSERT
    // when no row exists, so concurrent callers cannot both win.
    Task<ISyncRunLockHandle?> TryAcquireAsync(string pairId, TimeSpan ttl, string? owner = null, CancellationToken ct = default);
}

// Release-on-dispose handle for an acquired run lock. Held for the whole read+mirror and
// released on dispose; there is intentionally no mid-run renewal (see the TTL contract on
// ISyncRunLock — the TTL is sized to exceed a single mirror's worst-case duration).
public interface ISyncRunLockHandle : IAsyncDisposable
{
    string PairId { get; }
}
