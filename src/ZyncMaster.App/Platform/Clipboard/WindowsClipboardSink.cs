using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using ZyncMaster.Core;
using ZyncMaster.Engine;

namespace ZyncMaster.App.Platform.Clipboard;

// Windows IClipboardSink: writes a received entry to the OS clipboard (text via CF_UNICODETEXT,
// image via CF_DIB), or pastes it straight into a target window.
//
// PasteIntoFocusedAsync prefers the EXPLICIT target window the caller captured (the clipboard
// viewer records the foreground HWND before it steals focus — by the time the paste runs, the
// foreground window is the viewer itself, so capturing it here would paste into the wrong window).
// Only when no target was captured does it fall back to the live foreground window. It sets the
// clipboard, re-asserts the target as foreground, waits a short beat for the activation to land,
// then synthesizes Ctrl+V with SendInput so the target app performs its own paste.
//
// Untested process boundary (Win32 clipboard + SendInput). Best-effort throughout; the optional
// logger makes the failure modes visible (clipboard write refused, SendInput threw) instead of
// silently "nothing happened".
[SupportedOSPlatform("windows")]
public sealed class WindowsClipboardSink : IClipboardSink
{
    // How long to let the SetForegroundWindow activation settle before the synthetic Ctrl+V.
    // Sending the chord in the same instant races the focus switch and the keystroke lands in the
    // old (often just-hidden) window — i.e. nowhere.
    private static readonly TimeSpan FocusSettleDelay = TimeSpan.FromMilliseconds(80);

    private readonly IAppLogger? _logger;

    public WindowsClipboardSink(IAppLogger? logger = null)
    {
        _logger = logger;
    }

    public Task SetAsync(ClipboardEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!WriteToClipboard(entry))
            _logger?.Log(LogLevel.Warning, "Clipboard sink: the OS clipboard write failed (no content, or the clipboard was locked by another process).");
        return Task.CompletedTask;
    }

    public async Task<bool> PasteIntoFocusedAsync(ClipboardEntry entry, nint targetWindow, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Prefer the caller-captured target (the viewer's pre-open foreground window); fall back to
        // the live foreground only when nothing was captured.
        var target = targetWindow != 0 ? (IntPtr)targetWindow : Win32.GetForegroundWindow();

        if (!WriteToClipboard(entry))
        {
            _logger?.Log(LogLevel.Warning, "Clipboard paste: the OS clipboard write failed (no content, or the clipboard was locked by another process).");
            return false;
        }

        if (target == IntPtr.Zero)
        {
            // Content is on the clipboard but there is nowhere to send Ctrl+V — a manual paste works.
            _logger?.Log(LogLevel.Warning, "Clipboard paste: no target window to paste into; the content was left on the clipboard.");
            return true;
        }

        Win32.SetForegroundWindow(target);

        // Give the activation a beat to land so the synthetic Ctrl+V reaches the target, not the
        // window that was foreground a moment ago (typically the just-hidden viewer).
        try { await Task.Delay(FocusSettleDelay, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return true; /* clipboard is set; only the keystroke was skipped */ }

        SendCtrlV();
        return true;
    }

    private static bool WriteToClipboard(ClipboardEntry entry)
    {
        if (entry.Type == ClipboardEntryType.Text)
            return entry.Text is not null && Win32Clipboard.TryWriteText(entry.Text);

        return entry.ImageBytes is { Length: > 0 } && Win32Clipboard.TryWriteImageDib(entry.ImageBytes);
    }

    private void SendCtrlV()
    {
        try
        {
            var inputs = new[]
            {
                KeyDown(Win32.VK_CONTROL),
                KeyDown(Win32.VK_V),
                KeyUp(Win32.VK_V),
                KeyUp(Win32.VK_CONTROL),
            };
            Win32.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<Win32.INPUT>());
        }
        catch (Exception ex)
        {
            // Best-effort: a failed synthetic paste leaves the content on the clipboard for a manual
            // paste — but it must be visible in the log, not a silent nothing.
            _logger?.Log(LogLevel.Warning, "Clipboard paste: synthesizing Ctrl+V failed; the content stays on the clipboard for a manual paste.", ex);
        }
    }

    private static Win32.INPUT KeyDown(ushort vk) => MakeKey(vk, 0);
    private static Win32.INPUT KeyUp(ushort vk) => MakeKey(vk, Win32.KEYEVENTF_KEYUP);

    private static Win32.INPUT MakeKey(ushort vk, uint flags) => new()
    {
        type = Win32.INPUT_KEYBOARD,
        u = new Win32.InputUnion
        {
            ki = new Win32.KEYBDINPUT
            {
                wVk = vk,
                dwFlags = flags,
            },
        },
    };
}
