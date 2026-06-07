using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace ZyncMaster.Engine;

// Echo suppression. When this device applies a received item to the OS clipboard, the OS capture
// source fires a "new copy" event for that very content. Without suppression we would re-publish it,
// bouncing it back to the sender (and looping). Before applying, the caller records the content hash
// via MarkApplied; when the resulting capture arrives, IsEcho returns true exactly once and the
// caller drops it. The match is consumed so a later, genuine re-copy of the same content by the user
// is still published.
//
// The recent-applied set is bounded (FIFO eviction) so it can't grow without limit on a busy device.
public sealed class ClipboardDedupe
{
    private const int DefaultCapacity = 16;

    private readonly int _capacity;
    private readonly LinkedList<string> _order = new();
    private readonly HashSet<string> _recent = new(StringComparer.Ordinal);

    public ClipboardDedupe(int capacity = DefaultCapacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
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

    // Records that we just wrote this content to the OS clipboard, so its echo capture is expected.
    public void MarkApplied(string hash)
    {
        ArgumentNullException.ThrowIfNull(hash);

        if (_recent.Add(hash))
        {
            _order.AddLast(hash);
            if (_recent.Count > _capacity)
            {
                var oldest = _order.First!.Value;
                _order.RemoveFirst();
                _recent.Remove(oldest);
            }
        }
    }

    // True if this hash matches a recently-applied content; consumed on match so a second call for the
    // same hash returns false.
    public bool IsEcho(string hash)
    {
        ArgumentNullException.ThrowIfNull(hash);

        if (!_recent.Remove(hash))
            return false;

        _order.Remove(hash);
        return true;
    }
}
