using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace ZyncMaster.App.Platform.Clipboard;

// A message-only (HWND_MESSAGE) window running its own GetMessage pump on a dedicated background
// thread. Win32 clipboard-format listeners and global hotkeys both need an HWND with a live message
// loop to deliver WM_CLIPBOARDUPDATE / WM_HOTKEY; the App's UI is a WebView2 host with no spare
// top-level Win32 window we can hijack, so each platform clipboard component owns one of these.
//
// Untested process boundary (Win32 + a background pump). Best-effort: the WndProc swallows handler
// exceptions so a single bad message can never tear the pump down.
[SupportedOSPlatform("windows")]
internal sealed class MessageOnlyWindow : IDisposable
{
    private readonly Win32.WndProc _wndProc;     // kept alive for the lifetime of the window
    private readonly Action<uint, IntPtr, IntPtr> _onMessage;
    private readonly string _className;
    private readonly ManualResetEventSlim _ready = new(false);
    private Thread? _thread;
    private IntPtr _hwnd = IntPtr.Zero;
    private volatile bool _disposed;

    public IntPtr Handle => _hwnd;

    // onMessage receives (msg, wParam, lParam) on the pump thread for every dispatched message.
    public MessageOnlyWindow(Action<uint, IntPtr, IntPtr> onMessage)
    {
        _onMessage = onMessage ?? throw new ArgumentNullException(nameof(onMessage));
        _wndProc = WndProcImpl;
        _className = "ZyncMasterClipboardMsgWnd_" + Guid.NewGuid().ToString("N");
    }

    // Starts the pump thread and blocks until the HWND exists (or creation failed).
    public void Start()
    {
        if (_thread is not null)
            return;

        _thread = new Thread(Pump)
        {
            IsBackground = true,
            Name = "ZyncMaster.Clipboard.MsgPump",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    private void Pump()
    {
        try
        {
            var wc = new Win32.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = Marshal.GetHINSTANCE(typeof(MessageOnlyWindow).Module),
                lpszClassName = _className,
            };
            Win32.RegisterClassEx(ref wc);

            _hwnd = Win32.CreateWindowEx(
                0, _className, null, 0, 0, 0, 0, 0,
                (IntPtr)Win32.HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        }
        finally
        {
            _ready.Set();
        }

        if (_hwnd == IntPtr.Zero)
            return;

        while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessage(ref msg);
        }
    }

    private IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Win32.WM_DESTROY)
        {
            Win32.PostQuitMessage(0);
            return IntPtr.Zero;
        }

        try
        {
            _onMessage(msg, wParam, lParam);
        }
        catch
        {
            // Best-effort: never let a handler exception escape into the Win32 pump.
        }

        return Win32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        var hwnd = _hwnd;
        if (hwnd != IntPtr.Zero)
        {
            // Tearing the window down posts WM_DESTROY -> PostQuitMessage, which ends the pump loop.
            Win32.PostMessage(hwnd, Win32.WM_DESTROY, IntPtr.Zero, IntPtr.Zero);
            _hwnd = IntPtr.Zero;
        }

        try { _thread?.Join(2000); } catch { /* best-effort */ }
        _ready.Dispose();
    }
}
