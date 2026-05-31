using System.Collections.Concurrent;

namespace ZyncMaster.Server;

// In-memory run lock for tests and the pre-DB path. The compare-and-swap on the per-pair
// entry mirrors the EF store's atomic `UPDATE ... WHERE LockedUntil < now`: a live lock
// blocks; an expired or absent one is acquired. A lock object scopes the CAS so two
// concurrent callers cannot both win.
public sealed class InMemorySyncRunLock : ISyncRunLock
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _locks =
        new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Task<ISyncRunLockHandle?> TryAcquireAsync(
        string pairId, TimeSpan ttl, string? owner = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pairId)) throw new ArgumentException("pairId required.", nameof(pairId));

        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            if (_locks.TryGetValue(pairId, out var until) && until > now)
                return Task.FromResult<ISyncRunLockHandle?>(null); // live lock held elsewhere

            _locks[pairId] = now.Add(ttl);
            return Task.FromResult<ISyncRunLockHandle?>(new Handle(this, pairId));
        }
    }

    private void Renew(string pairId, TimeSpan ttl)
    {
        lock (_gate)
            _locks[pairId] = DateTimeOffset.UtcNow.Add(ttl);
    }

    private void Release(string pairId)
    {
        lock (_gate)
            _locks[pairId] = DateTimeOffset.UnixEpoch;
    }

    private sealed class Handle : ISyncRunLockHandle
    {
        private readonly InMemorySyncRunLock _owner;
        private bool _released;

        public Handle(InMemorySyncRunLock owner, string pairId)
        {
            _owner = owner;
            PairId = pairId;
        }

        public string PairId { get; }

        public Task RenewAsync(TimeSpan ttl, CancellationToken ct = default)
        {
            if (!_released) _owner.Renew(PairId, ttl);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (!_released)
            {
                _released = true;
                _owner.Release(PairId);
            }
            return ValueTask.CompletedTask;
        }
    }
}
