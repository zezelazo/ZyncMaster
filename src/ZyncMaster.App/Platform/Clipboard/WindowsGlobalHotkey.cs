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

    // Tried in order when the configured hotkey can't be registered (already taken / unparseable).
    // The canonical "already owned by another app" failure is win32Error 1409
    // (ERROR_HOTKEY_ALREADY_REGISTERED); we fall back on any registration failure, not only that one.
    // Deliberately short and Ctrl-anchored so the viewer hotkey stays alive on a best-effort basis.
    internal static readonly string[] FallbackHotkeys =
    {
        "Ctrl+Win+Q",
        "Ctrl+Shift+Q",
        "Ctrl+Alt+V",
        "Ctrl+Win+V",
    };

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

    // The hotkey string that actually got registered (after any fallback), or null when nothing is
    // currently bound. Callers / UI can read this to show the user which combo is live.
    public string? ActiveHotkey { get; private set; }

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

        _window ??= CreateWindow();
        if (_window.Handle == IntPtr.Zero)
            return;

        // Try the configured hotkey first; if it can't be registered (already owned by another app, or
        // unparseable), fall back through a short ordered list so the viewer hotkey stays alive instead
        // of going dead. SelectHotkey does the parse + ordered attempts; the actual RegisterHotKey runs
        // on the pump thread that owns the HWND (RegisterHotKey fails cross-thread otherwise).
        var result = SelectHotkey(hotkey, FallbackHotkeys, TryRegisterOnPumpThread);

        if (result.RegisteredHotkey is { } active)
        {
            _registered = true;
            ActiveHotkey = active;

            if (string.Equals(active, hotkey, StringComparison.OrdinalIgnoreCase))
                _logger?.Log(LogLevel.Info,
                    $"Clipboard hotkey registered: '{active}' (pumpThread={_window.PumpThreadId}).");
            else
                _logger?.Log(LogLevel.Info,
                    $"Clipboard hotkey '{hotkey}' unavailable; registered fallback '{active}' " +
                    $"(pumpThread={_window.PumpThreadId}).");
        }
        else
        {
            _registered = false;
            ActiveHotkey = null;
            _logger?.Log(LogLevel.Warning,
                $"RegisterHotKey FAILED for '{hotkey}' and all fallbacks " +
                $"({string.Join(", ", FallbackHotkeys)}); viewer hotkey is not bound " +
                $"(lastWin32Error={result.LastWin32Error}).");
        }
    }

    // Runs RegisterHotKey on the pump thread that OWNS the HWND (mandatory thread affinity) and reports
    // the outcome + captured Win32 error. MOD_NOREPEAT so holding the combo fires once, not a stream.
    private RegisterAttempt TryRegisterOnPumpThread(uint modifiers, uint vk)
    {
        var window = _window;
        if (window is null || window.Handle == IntPtr.Zero)
            return new RegisterAttempt(false, 0);

        var lastError = 0;
        var ok = window.Invoke(() =>
        {
            var registered = Win32.RegisterHotKey(window.Handle, HotkeyId, modifiers | Win32.MOD_NOREPEAT, vk);
            if (!registered) lastError = Marshal.GetLastWin32Error();
            return (IntPtr)(registered ? 1 : 0);
        }) != IntPtr.Zero;

        return new RegisterAttempt(ok, ok ? 0 : lastError);
    }

    // Outcome of a single RegisterHotKey attempt: whether it succeeded and the Win32 error if not.
    internal readonly record struct RegisterAttempt(bool Success, int Win32Error);

    // Result of running the ordered selection: the hotkey string that registered (null = none did) and
    // the last Win32 error seen across all attempts (for diagnostics on total failure).
    internal readonly record struct HotkeySelection(string? RegisteredHotkey, int LastWin32Error);

    // Pure selection logic, decoupled from Win32 so it can be unit-tested with a fake `attempt` delegate.
    // Tries `primary` then each entry of `fallbacks` (skipping duplicates / unparseable combos), invoking
    // `attempt` only for parseable candidates, and stops at the first success. Returns which one won plus
    // the last Win32 error observed (so an all-fail caller can log it).
    internal static HotkeySelection SelectHotkey(
        string primary,
        IReadOnlyList<string> fallbacks,
        Func<uint, uint, RegisterAttempt> attempt)
    {
        ArgumentNullException.ThrowIfNull(fallbacks);
        ArgumentNullException.ThrowIfNull(attempt);

        var lastError = 0;
        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var candidates = new List<string>(fallbacks.Count + 1) { primary };
        candidates.AddRange(fallbacks);

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !tried.Add(candidate.Trim()))
                continue;

            if (!TryParse(candidate, out var modifiers, out var vk))
                continue;

            var outcome = attempt(modifiers, vk);
            if (outcome.Success)
                return new HotkeySelection(candidate, 0);

            lastError = outcome.Win32Error;
        }

        return new HotkeySelection(null, lastError);
    }

    public void Unregister()
    {
        if (_registered && _window is { Handle: var h } && h != IntPtr.Zero)
            // UnregisterHotKey must also run on the owning pump thread (same affinity rule as Register).
            _window.Invoke(() => (IntPtr)(Win32.UnregisterHotKey(h, HotkeyId) ? 1 : 0));
        _registered = false;
        ActiveHotkey = null;
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
