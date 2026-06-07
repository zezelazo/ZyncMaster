using System.Threading;
using System.Threading.Tasks;

namespace ZyncMaster.Engine;

// Writes a received entry back to the OS clipboard, or pastes it into the focused window.
public interface IClipboardSink
{
    Task SetAsync(ClipboardEntry entry, CancellationToken ct = default);

    // Returns true when the entry was actually written to the OS clipboard (and a paste synthesized),
    // false when there was nothing to write (e.g. a Text entry whose plaintext was unavailable or an
    // image with no bytes) — so callers can avoid reporting a no-op as a successful paste.
    Task<bool> PasteIntoFocusedAsync(ClipboardEntry entry, CancellationToken ct = default);
}
