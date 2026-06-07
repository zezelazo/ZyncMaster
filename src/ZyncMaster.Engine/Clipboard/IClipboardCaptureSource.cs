namespace ZyncMaster.Engine;

// Watches the OS clipboard and raises Captured for each new local copy. Platform-specific
// implementations live in the App; the Engine only depends on this port.
public interface IClipboardCaptureSource
{
    event Action<ClipboardEntry> Captured;
    void Start();
    void Stop();
}
