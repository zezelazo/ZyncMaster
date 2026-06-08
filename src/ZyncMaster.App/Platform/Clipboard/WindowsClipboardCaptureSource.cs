using System;
using System.Runtime.Versioning;
using ZyncMaster.Engine;

namespace ZyncMaster.App.Platform.Clipboard;

// Windows IClipboardCaptureSource: a message-only window registers an AddClipboardFormatListener and
// raises Captured for each local copy. On WM_CLIPBOARDUPDATE it reads CF_UNICODETEXT (text) or
// CF_DIB (image) via the Win32 clipboard and builds a fresh ClipboardEntry stamped with this
// device's identity.
//
// The origin identity (deviceId / deviceName) is supplied through a Func because the App only knows
// its deviceId after the device has registered with the server; the capture source can be
// constructed early and will pull the current identity on each capture. If identity is not yet
// known the Func may return an empty id and the ClipboardService still encrypts before publish — the
// server fills in/validates origin from the api key.
//
// Untested process boundary (Win32). Best-effort: a read that fails or yields nothing is dropped,
// never thrown.
[SupportedOSPlatform("windows")]
public sealed class WindowsClipboardCaptureSource : IClipboardCaptureSource, IDisposable
{
    private readonly Func<(string id, string? name)> _origin;
    private readonly Func<DateTimeOffset> _now;
    private MessageOnlyWindow? _window;
    private bool _started;

    public event Action<ClipboardEntry>? Captured;

    public WindowsClipboardCaptureSource(Func<(string id, string? name)> origin, Func<DateTimeOffset>? now = null)
    {
        _origin = origin ?? throw new ArgumentNullException(nameof(origin));
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public void Start()
    {
        if (_started)
            return;
        _started = true;

        _window = new MessageOnlyWindow(OnMessage);
        _window.Start();
        if (_window.Handle != IntPtr.Zero)
            Win32.AddClipboardFormatListener(_window.Handle);
    }

    public void Stop()
    {
        if (!_started)
            return;
        _started = false;

        var window = _window;
        if (window is not null)
        {
            if (window.Handle != IntPtr.Zero)
                Win32.RemoveClipboardFormatListener(window.Handle);
            window.Dispose();
            _window = null;
        }
    }

    private void OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != Win32.WM_CLIPBOARDUPDATE)
            return;

        var entry = BuildEntry();
        if (entry is not null)
            Captured?.Invoke(entry);
    }

    private ClipboardEntry? BuildEntry()
    {
        var (id, name) = SafeOrigin();

        // Prefer text; only fall back to an image when there is no text on the board.
        var text = Win32Clipboard.TryReadText();
        if (!string.IsNullOrEmpty(text))
        {
            return new ClipboardEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = ClipboardEntryType.Text,
                Text = text,
                CreatedUtc = _now(),
                OriginDeviceId = id,
                OriginDeviceName = name,
            };
        }

        var dib = Win32Clipboard.TryReadImageDib();
        if (dib is { Length: > 0 })
        {
            // ImageBytes stays the raw CF_DIB (the server stores it verbatim). Additionally build a
            // small downscaled PNG so the viewer shows a real preview; a failed/oversize decode just
            // leaves Thumbnail null (typed tile only). DibThumbnailEncoder is best-effort and never
            // throws, so capture is never blocked by a bad image.
            return new ClipboardEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = ClipboardEntryType.Image,
                ImageBytes = dib,
                Thumbnail = DibThumbnailEncoder.TryCreatePngThumbnail(dib),
                SizeBytes = dib.Length,
                CreatedUtc = _now(),
                OriginDeviceId = id,
                OriginDeviceName = name,
            };
        }

        return null;
    }

    private (string id, string? name) SafeOrigin()
    {
        try
        {
            var (id, name) = _origin();
            return (id ?? "", name);
        }
        catch
        {
            return ("", null);
        }
    }

    public void Dispose() => Stop();
}
