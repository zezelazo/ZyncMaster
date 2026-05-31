using System.Security.Cryptography;

namespace ZyncMaster.Server;

// In-memory, single-instance one-time handle store (plan v2 §A-1 / §C-7). Thread-safe via a
// lock around the dictionary. Handles live 60s and are deleted on first consume.
//
// SINGLE-INSTANCE ASSUMPTION (documented on the interface): persisting to a DB is required
// before scaling out. A TimeProvider seam makes expiry deterministic in tests.
public sealed class InMemoryIdentityHandleStore : IIdentityHandleStore
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _handles = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    public InMemoryIdentityHandleStore(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    public string IssueHandle(string identityAccessToken)
    {
        ArgumentNullException.ThrowIfNull(identityAccessToken);
        var handle = NewHandle();
        var expiresAt = _clock.GetUtcNow().Add(Ttl);
        lock (_gate)
        {
            _handles[handle] = new Entry(identityAccessToken, expiresAt);
        }
        return handle;
    }

    public string? ConsumeHandle(string handle)
    {
        if (string.IsNullOrEmpty(handle))
        {
            return null;
        }

        lock (_gate)
        {
            if (!_handles.TryGetValue(handle, out var entry))
            {
                return null;
            }

            // One-time: always remove on lookup, whether live or expired.
            _handles.Remove(handle);

            return entry.ExpiresAt <= _clock.GetUtcNow() ? null : entry.Token;
        }
    }

    // 32-char random handle (24 bytes -> 32 base64url chars, no padding).
    private static string NewHandle()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private readonly record struct Entry(string Token, DateTimeOffset ExpiresAt);
}
