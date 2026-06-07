using System.Threading;
using System.Threading.Tasks;

namespace ZyncMaster.Engine;

// Writes a received entry back to the OS clipboard, or pastes it into the focused window.
public interface IClipboardSink
{
    Task SetAsync(ClipboardEntry entry, CancellationToken ct = default);
    Task PasteIntoFocusedAsync(ClipboardEntry entry, CancellationToken ct = default);
}
