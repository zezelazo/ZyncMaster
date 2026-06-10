using System.Threading;
using System.Threading.Tasks;

namespace ZyncMaster.Engine;

// Writes a received entry back to the OS clipboard, or pastes it into a target window.
public interface IClipboardSink
{
    Task SetAsync(ClipboardEntry entry, CancellationToken ct = default);

    // Returns true when the entry was actually written to the OS clipboard (and a paste synthesized),
    // false when there was nothing to write (e.g. a Text entry whose plaintext was unavailable or an
    // image with no bytes) — so callers can avoid reporting a no-op as a successful paste.
    //
    // targetWindow is the native handle of the window the synthesized paste must land in (on Windows,
    // the HWND the clipboard viewer captured BEFORE it stole focus). When the viewer triggers the
    // paste, the live foreground window IS the viewer, so capturing the foreground at paste time
    // targets the wrong window — the caller passes the real target instead. Zero means "no captured
    // target": the sink falls back to the window that is foreground when the paste runs.
    Task<bool> PasteIntoFocusedAsync(ClipboardEntry entry, nint targetWindow, CancellationToken ct = default);
}
