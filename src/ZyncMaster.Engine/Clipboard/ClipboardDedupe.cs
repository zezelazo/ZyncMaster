using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace ZyncMaster.Engine;

// Duplicate suppression for the clipboard pipeline. Two independent time windows:
//
//  * Echo window (MarkApplied / IsEcho). When this device writes a received or pasted item to the
//    OS clipboard, Windows fires WM_CLIPBOARDUPDATE for that very content — and not just once: a
//    single programmatic set commonly fires the event 2-3 times, and an RDP session with clipboard
//    redirection re-announces the set yet again. A consume-on-first-match scheme therefore only
//    swallows the FIRST fire; the second one is captured as a "new copy", published, applied by the
//    peer, whose own multi-fire bounces it back — a feedback loop that ping-pongs the same content
//    between two machines forever. Instead, MarkApplied opens a TTL window during which EVERY
//    capture of that hash counts as an echo (unlimited matches). A genuine re-copy of the same
//    content by the user after the window has passed is still published.
//
//  * Recent-publish window (MarkPublished / IsRecentlyPublished). The multi-fire above also hits
//    ORDINARY user copies: one Ctrl+C can surface as 2-3 capture events, and users re-copy the
//    same content in quick succession. The publish path records the LAST published hash and drops
//    any capture of that same hash within the window, so the same content is sent to the server at
//    most once per window. The window is deliberately single-slot: the WM_CLIPBOARDUPDATE fires
//    for one clipboard set are contiguous, so once a DIFFERENT hash is published the previous
//    burst is over — copying A, then B, then A again is three deliberate user actions and all
//    three must reach the server (this mirrors the server's head-only dedupe). A multi-entry map
//    here once swallowed the second A for the full TTL while the peers kept B: silently wrong sync.
//
//  * OnNewContentCaptured bridges the two windows: when a capture with a NEW hash passes the
//    gates and is about to publish, every OTHER applied-echo entry is invalidated. Same reasoning —
//    the echo burst for previously applied content cannot fire after the clipboard has moved on,
//    so a later re-copy of that earlier content is a fresh user action, not an echo.
//
// Time is measured on the injected TimeProvider's monotonic timestamp (testable, immune to
// wall-clock jumps). The applied map is bounded with FIFO eviction so it cannot grow without
// limit on a busy device.
public sealed class ClipboardDedupe
{
    private const int DefaultCapacity = 16;

    // How long after an apply every capture of the same content is still treated as our own echo.
    public static readonly TimeSpan AppliedEchoTtl = TimeSpan.FromSeconds(15);

    // How long after a publish a capture of identical content is dropped as a duplicate.
    public static readonly TimeSpan RecentPublishTtl = TimeSpan.FromSeconds(20);

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly TtlMap _applied;

    // Single-slot recent-publish window: only the most recently published hash is tracked. See the
    // class comment for why this must NOT be a multi-entry map.
    private string? _lastPublishedHash;
    private long _lastPublishedStamp;

    public ClipboardDedupe(int capacity = DefaultCapacity, TimeProvider? timeProvider = null)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _time = timeProvider ?? TimeProvider.System;
        _applied = new TtlMap(capacity, AppliedEchoTtl, _time);
    }

    // Stable SHA-256 over the entry type plus its text bytes (UTF-8) or image bytes. Type is mixed in
    // so identical bytes under a different type hash differently.
    public string Hash(ClipboardEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var payload = entry.Type == ClipboardEntryType.Image
            ? entry.ImageBytes ?? Array.Empty<byte>()
            : Encoding.UTF8.GetBytes(entry.Text ?? string.Empty);

        var buffer = new byte[1 + payload.Length];
        buffer[0] = (byte)entry.Type;
        Buffer.BlockCopy(payload, 0, buffer, 1, payload.Length);

        return Convert.ToHexString(SHA256.HashData(buffer));
    }

    // Records that we just wrote this content to the OS clipboard; every capture of it within the
    // echo window is expected (the OS multi-fires) and must be dropped. Re-marking refreshes the window.
    public void MarkApplied(string hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        lock (_gate) _applied.Mark(hash);
    }

    // True while the hash is inside the echo window opened by MarkApplied. NOT consumed on match —
    // a single programmatic clipboard set fires WM_CLIPBOARDUPDATE several times (more under RDP
    // clipboard redirection) and every one of those fires must be recognized as the same echo.
    public bool IsEcho(string hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        lock (_gate) return _applied.Contains(hash);
    }

    // Records that this content was just published to the server. Replaces the previous slot:
    // publishing different content ends the previous content's duplicate window immediately.
    public void MarkPublished(string hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        lock (_gate)
        {
            _lastPublishedHash = hash;
            _lastPublishedStamp = _time.GetTimestamp();
        }
    }

    // True only while the hash equals the LAST published one and that publish is inside the
    // window: the same content was already sent and a second send would only duplicate history
    // (multi-fired capture or a Ctrl+C mash). A different hash published in between resets the
    // slot, so an A-B-A re-copy of A is a legitimate new publish, never a duplicate.
    public bool IsRecentlyPublished(string hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        lock (_gate)
        {
            return _lastPublishedHash is not null
                && string.Equals(_lastPublishedHash, hash, StringComparison.Ordinal)
                && _time.GetElapsedTime(_lastPublishedStamp) <= RecentPublishTtl;
        }
    }

    // Called when a capture with this hash has passed the echo and recent-publish gates and is
    // about to be published: the clipboard has moved on, so any echo window still open for OTHER
    // content can no longer multi-fire (those fires are contiguous with their clipboard set). The
    // entry for this very hash — if a concurrent apply just opened one — is left untouched.
    public void OnNewContentCaptured(string hash)
    {
        ArgumentNullException.ThrowIfNull(hash);
        lock (_gate) _applied.RemoveAllExcept(hash);
    }

    // Bounded hash -> monotonic-timestamp map with a fixed TTL. FIFO eviction on overflow; expired
    // entries are pruned opportunistically on every Mark/Contains. Not thread-safe by itself — the
    // owner serializes access.
    private sealed class TtlMap
    {
        private readonly int _capacity;
        private readonly TimeSpan _ttl;
        private readonly TimeProvider _time;
        private readonly Dictionary<string, long> _stamps = new(StringComparer.Ordinal);
        private readonly LinkedList<string> _order = new();

        public TtlMap(int capacity, TimeSpan ttl, TimeProvider time)
        {
            _capacity = capacity;
            _ttl = ttl;
            _time = time;
        }

        public void Mark(string hash)
        {
            PruneExpired();

            if (_stamps.ContainsKey(hash))
                _order.Remove(hash); // refresh: move to the back with a fresh timestamp
            _stamps[hash] = _time.GetTimestamp();
            _order.AddLast(hash);

            while (_stamps.Count > _capacity)
            {
                var oldest = _order.First!.Value;
                _order.RemoveFirst();
                _stamps.Remove(oldest);
            }
        }

        public bool Contains(string hash)
        {
            PruneExpired();
            return _stamps.ContainsKey(hash);
        }

        // Drops every entry except the given hash (which keeps its original timestamp).
        public void RemoveAllExcept(string hash)
        {
            var node = _order.First;
            while (node is not null)
            {
                var next = node.Next;
                if (!string.Equals(node.Value, hash, StringComparison.Ordinal))
                {
                    _stamps.Remove(node.Value);
                    _order.Remove(node);
                }
                node = next;
            }
        }

        private void PruneExpired()
        {
            while (_order.First is { } first
                   && _time.GetElapsedTime(_stamps[first.Value]) > _ttl)
            {
                _stamps.Remove(first.Value);
                _order.RemoveFirst();
            }
        }
    }
}
