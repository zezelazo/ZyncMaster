using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ZyncMaster.Core;
using ZyncMaster.Engine;

namespace ZyncMaster.App.Platform.Clipboard;

// Windows IClipboardHotkey: parses a hotkey string like "Ctrl+Win+Q" into RegisterHotKey modifiers +
// a virtual-key code, registers it against a message-only window, and raises Pressed on WM_HOTKEY.
// Re-registering replaces the previous binding (unregister first), so the user can rebind the viewer
// hotkey at runtime.
//
// Untested process boundary (Win32). Best-effort: an unparseable or already-taken combo is ignored
// (Pressed simply never fires) rather than throwing into the App.
[SupportedOSPlatform("windows")]
public sealed class WindowsGlobalHotkey : IClipboardHotkey, IDisposable
{
    private const int HotkeyId = 0xB001;

    private static readonly Dictionary<string, ushort> NamedVk = new(StringComparer.OrdinalIgnoreCase)
    {
        ["space"] = 0x20,
        ["enter"] = 0x0D,
        ["return"] = 0x0D,
        ["tab"] = 0x09,
        ["esc"] = 0x1B,
        ["escape"] = 0x1B,
        ["ins"] = 0x2D,
        ["insert"] = 0x2D,
        ["del"] = 0x2E,
        ["delete"] = 0x2E,
        ["home"] = 0x24,
        ["end"] = 0x23,
        ["pgup"] = 0x21,
        ["pgdn"] = 0x22,
    };

    private readonly IAppLogger? _logger;
    private MessageOnlyWindow? _window;
    private bool _registered;

    public event Action? Pressed;

    public WindowsGlobalHotkey(IAppLogger? logger = null)
    {
        _logger = logger;
    }

    public void Register(string hotkey)
    {
        if (string.IsNullOrWhiteSpace(hotkey))
            return;

        // Re-register: drop any previous binding first.
        Unregister();

        if (!TryParse(hotkey, out var modifiers, out var vk))
        {
            _logger?.Log(LogLevel.Warning, $"Clipboard hotkey '{hotkey}' could not be parsed; not registered.");
            return;
        }

        _window ??= CreateWindow();
        if (_window.Handle == IntPtr.Zero)
            return;

        // RegisterHotKey MUST run on the thread that OWNS the HWND — the message-only pump thread.
        // Calling it from the App's Task.Run/threadpool thread fails cross-thread (ERROR_WINDOW_OF_-
        // OTHER_THREAD 1408) and silently never registers, so the hotkey appears dead. Marshal it onto
        // the pump thread via MessageOnlyWindow.Invoke, and capture the Win32 error there too.
        // MOD_NOREPEAT so holding the combo fires once, not a stream.
        var lastError = 0;
        _registered = _window.Invoke(() =>
        {
            var ok = Win32.RegisterHotKey(_window.Handle, HotkeyId, modifiers | Win32.MOD_NOREPEAT, vk);
            if (!ok) lastError = Marshal.GetLastWin32Error();
            return (IntPtr)(ok ? 1 : 0);
        }) != IntPtr.Zero;

        if (_registered)
            _logger?.Log(LogLevel.Info, $"Clipboard hotkey registered: '{hotkey}' (pumpThread={_window.PumpThreadId}).");
        else
            _logger?.Log(LogLevel.Warning,
                $"RegisterHotKey FAILED for '{hotkey}' (vk={vk}, hwnd=0x{_window.Handle:X}, win32Error={lastError}).");
    }

    public void Unregister()
    {
        if (_registered && _window is { Handle: var h } && h != IntPtr.Zero)
            // UnregisterHotKey must also run on the owning pump thread (same affinity rule as Register).
            _window.Invoke(() => (IntPtr)(Win32.UnregisterHotKey(h, HotkeyId) ? 1 : 0));
        _registered = false;
    }

    private MessageOnlyWindow CreateWindow()
    {
        var window = new MessageOnlyWindow(OnMessage);
        window.Start();
        return window;
    }

    private void OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Win32.WM_HOTKEY && wParam == (IntPtr)HotkeyId)
            Pressed?.Invoke();
    }

    // Parses "Ctrl+Win+Q" / "Alt+Shift+V" etc. The last token is the key; the rest are modifiers.
    internal static bool TryParse(string hotkey, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;

        var parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return false;

        ushort key = 0;
        var sawKey = false;

        foreach (var raw in parts)
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= Win32.MOD_CONTROL;
                    break;
                case "alt":
                    modifiers |= Win32.MOD_ALT;
                    break;
                case "shift":
                    modifiers |= Win32.MOD_SHIFT;
                    break;
                case "win":
                case "windows":
                case "meta":
                case "cmd":
                    modifiers |= Win32.MOD_WIN;
                    break;
                default:
                    if (!TryParseKey(raw, out key))
                        return false;
                    sawKey = true;
                    break;
            }
        }

        if (!sawKey || modifiers == 0)
            return false; // a global hotkey needs at least one modifier plus a key.

        vk = key;
        return true;
    }

    private static bool TryParseKey(string token, out ushort vk)
    {
        vk = 0;

        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'Z' || c is >= '0' and <= '9')
            {
                vk = c; // VK codes for A-Z / 0-9 equal their ASCII uppercase values.
                return true;
            }
        }

        // Function keys F1..F24 -> 0x70..0x87.
        if ((token.Length == 2 || token.Length == 3) &&
            (token[0] is 'f' or 'F') &&
            int.TryParse(token.AsSpan(1), out var fn) && fn is >= 1 and <= 24)
        {
            vk = (ushort)(0x70 + (fn - 1));
            return true;
        }

        if (NamedVk.TryGetValue(token, out var named))
        {
            vk = named;
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        Unregister();
        _window?.Dispose();
        _window = null;
    }
}
