using System;
using System.Runtime.Versioning;
using ZyncMaster.Core;
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
    private readonly IAppLogger _logger;
    private MessageOnlyWindow? _window;
    private bool _started;

    public event Action<ClipboardEntry>? Captured;

    public WindowsClipboardCaptureSource(
        Func<(string id, string? name)> origin,
        Func<DateTimeOffset>? now = null,
        IAppLogger? logger = null)
    {
        _origin = origin ?? throw new ArgumentNullException(nameof(origin));
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _logger = logger ?? NullAppLogger.Instance;
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
        {
            Captured?.Invoke(entry);
            return;
        }

        // A clipboard update fired but nothing was extractable. This is the symptom behind "copying
        // an image produced nothing": an image format we cannot read (no CF_DIB / no readable
        // CF_BITMAP, e.g. a private or PNG-only payload) is dropped here. Surface it at Warning so the
        // gap is visible in the daily log instead of failing silently.
        _logger.Log(LogLevel.Warning,
            "Clipboard update fired but no text or readable image was on the clipboard " +
            $"(formats: {Win32Clipboard.DescribeAvailableFormats()}). Item dropped.");
    }

    // File-drop capture cap: a file larger than this syncs as metadata only (no bytes). Matches the
    // server's ClipboardOptions.MaxBlobBytes default (100 MB); the server enforces its own cap on upload.
    private const long MaxFileBytes = 100L * 1024 * 1024;

    private ClipboardEntry? BuildEntry()
    {
        var (id, name) = SafeOrigin();

        // File(s) copied in Explorer ride CF_HDROP. Capture the FIRST file before text/image — a file
        // copy is the most specific intent. Its bytes ride along only within the cap; a bigger (or
        // unreadable) file is captured as metadata only and shows as a "too large to sync" entry.
        var file = Win32Clipboard.TryReadFileDrop(MaxFileBytes);
        if (file is { } f)
        {
            var withBytes = f.Bytes is { Length: > 0 };
            _logger.Log(LogLevel.Warning,
                $"Clipboard capture: file '{f.Name}' ({f.Size} bytes, {(withBytes ? "syncing" : "metadata only — over the cap or unreadable")}).");
            return new ClipboardEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = ClipboardEntryType.File,
                FileName = f.Name,
                FileBytes = f.Bytes,
                SizeBytes = f.Size,
                CreatedUtc = _now(),
                OriginDeviceId = id,
                OriginDeviceName = name,
            };
        }

        // Prefer text; only fall back to an image when there is no text on the board.
        var text = Win32Clipboard.TryReadText();
        if (!string.IsNullOrEmpty(text))
        {
            // Logged at Warning, not Info, on purpose: the device logger ships at minLevel Warning by
            // default (only --verbose / ZYNCMASTER_VERBOSE=1 lowers it), and the whole point of this
            // line is to answer "was a copy actually captured?" from the daily log without enabling
            // verbose. It rides the same default-visible level as the "item dropped" warning below.
            _logger.Log(LogLevel.Warning, $"Clipboard capture: text ({text.Length} chars).");
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

        // Image: read the raw CF_DIB when present, else best-effort synthesize a DIB from CF_BITMAP
        // (apps that copy a bitmap handle but no CF_DIB — some browsers / editors — would otherwise
        // produce nothing). Either way the result is a DIB blob the rest of the pipeline understands.
        var dib = Win32Clipboard.TryReadImageDib();
        if (dib is { Length: > 0 })
        {
            // ImageBytes stays the raw CF_DIB (the server stores it verbatim). Additionally build a
            // small downscaled PNG so the viewer shows a real preview; a failed/oversize decode just
            // leaves Thumbnail null (typed tile only). DibThumbnailEncoder is best-effort and never
            // throws, so capture is never blocked by a bad image.
            var thumb = DibThumbnailEncoder.TryCreatePngThumbnail(dib);
            // Warning, not Info — same reason as the text path: stay visible in the default-level daily
            // log so a successful image capture is distinguishable from a drop without verbose logging.
            _logger.Log(LogLevel.Warning,
                $"Clipboard capture: image (CF_DIB, {dib.Length} bytes, thumbnail={(thumb is { Length: > 0 } ? $"{thumb.Length} bytes" : "none")}).");
            return new ClipboardEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Type = ClipboardEntryType.Image,
                ImageBytes = dib,
                Thumbnail = thumb,
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
