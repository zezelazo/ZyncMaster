using System;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using ZyncMaster.Engine;

namespace ZyncMaster.App.Platform.Clipboard;

// Windows IClipboardSink: writes a received entry to the OS clipboard (text via CF_UNICODETEXT,
// image via CF_DIB), or pastes it straight into whatever window currently has focus.
//
// PasteIntoFocusedAsync captures the foreground HWND FIRST (before we touch the clipboard or do
// anything that could steal focus), sets the clipboard, re-asserts that window as foreground, then
// synthesizes Ctrl+V with SendInput so the target app performs its own paste.
//
// Untested process boundary (Win32 clipboard + SendInput). Best-effort throughout.
[SupportedOSPlatform("windows")]
public sealed class WindowsClipboardSink : IClipboardSink
{
    public Task SetAsync(ClipboardEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        WriteToClipboard(entry);
        return Task.CompletedTask;
    }

    public Task PasteIntoFocusedAsync(ClipboardEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Capture the prior foreground window BEFORE any clipboard work so we paste back into the
        // user's actual target rather than our own viewer.
        var target = Win32.GetForegroundWindow();

        if (!WriteToClipboard(entry))
            return Task.CompletedTask;

        if (target != IntPtr.Zero)
        {
            Win32.SetForegroundWindow(target);
            SendCtrlV();
        }

        return Task.CompletedTask;
    }

    private static bool WriteToClipboard(ClipboardEntry entry)
    {
        if (entry.Type == ClipboardEntryType.Text)
            return entry.Text is not null && Win32Clipboard.TryWriteText(entry.Text);

        return entry.ImageBytes is { Length: > 0 } && Win32Clipboard.TryWriteImageDib(entry.ImageBytes);
    }

    private static void SendCtrlV()
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
        catch
        {
            // Best-effort: a failed synthetic paste leaves the content on the clipboard for a manual paste.
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
