using System.Collections.Concurrent;

namespace ZyncMaster.Server;

// In-memory run lock for tests and the pre-DB path. The compare-and-swap on the per-pair
// entry mirrors the EF store's atomic `UPDATE ... WHERE LockedUntil < now`: a live lock
// blocks; an expired or absent one is acquired. A lock object scopes the CAS so two
// concurrent callers cannot both win.
public sealed class InMemorySyncRunLock : ISyncRunLock
{
    // FIX B — the entry carries the current holder's fence token alongside its expiry, mirroring
    // the EF store's (LockedUntil, FenceToken) row. Release matches the fence so a stolen lock is
    // never clobbered by the previous holder's late Dispose.
    private readonly ConcurrentDictionary<string, Entry> _locks =
        new(StringComparer.Ordinal);
    private readonly object _gate = new();

    private readonly record struct Entry(DateTimeOffset Until, string Fence);

    public Task<ISyncRunLockHandle?> TryAcquireAsync(
        string pairId, TimeSpan ttl, string? owner = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pairId)) throw new ArgumentException("pairId required.", nameof(pairId));

        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_locks.TryGetValue(pairId, out var existing) && existing.Until > now)
                return Task.FromResult<ISyncRunLockHandle?>(null); // live lock held elsewhere

            var fence = Guid.NewGuid().ToString("N");
            _locks[pairId] = new Entry(now.Add(ttl), fence);
            return Task.FromResult<ISyncRunLockHandle?>(new Handle(this, pairId, fence));
        }
    }

    private void Release(string pairId, string fence)
    {
        lock (_gate)
        {
            // Only expire the row if it still carries OUR fence — a stolen lock (re-acquired by
            // another caller after our TTL lapsed) has a different fence and is left untouched.
            if (_locks.TryGetValue(pairId, out var existing) && existing.Fence == fence)
                _locks[pairId] = existing with { Until = DateTimeOffset.UnixEpoch };
        }
    }

    // FIX 2 — extend the entry's expiry to now+ttl, but ONLY while it still carries OUR fence (we
    // still hold the lock). Returns true on a successful renewal, false when the lock has been lost
    // (expired-and-stolen → different fence, or removed). Mirrors the EF store's fenced conditional
    // UPDATE so the in-memory and DB paths behave identically under the heartbeat.
    private bool Renew(string pairId, string fence, TimeSpan ttl)
    {
        lock (_gate)
        {
            if (_locks.TryGetValue(pairId, out var existing) && existing.Fence == fence)
            {
                _locks[pairId] = existing with { Until = DateTimeOffset.UtcNow.Add(ttl) };
                return true;
            }
            return false;
        }
    }

    private sealed class Handle : ISyncRunLockHandle
    {
        private readonly InMemorySyncRunLock _owner;
        private readonly string _fence;
        private bool _released;

        public Handle(InMemorySyncRunLock owner, string pairId, string fence)
        {
            _owner = owner;
            PairId = pairId;
            _fence = fence;
        }

        public string PairId { get; }

        public Task<bool> RenewAsync(TimeSpan ttl, CancellationToken ct = default)
            => Task.FromResult(!_released && _owner.Renew(PairId, _fence, ttl));

        public ValueTask DisposeAsync()
        {
            if (!_released)
            {
                _released = true;
                _owner.Release(PairId, _fence);
            }
            return ValueTask.CompletedTask;
        }
    }
}
