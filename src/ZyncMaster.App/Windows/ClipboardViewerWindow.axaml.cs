using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ZyncMaster.App.Windows;

namespace ZyncMaster.App.Windows;

// The clipboard quick-viewer popup. Small, frameless, TOP-MOST, sized for a 13" screen. The global
// hotkey toggles it: Open() captures the window that currently has focus FIRST (so a later paste can
// target it), then shows the viewer focused. It closes on Esc, after a paste, and on deactivation
// (focus loss). "Close" here means HIDE — the window instance is reused across hotkey presses so the
// embedded WebView2 (and its bridge) are created once.
//
// It hosts ui/clipboard-viewer.html through an IWebHost (the embedded WebView2 on Windows), mounted
// into HostPanel exactly like MainWindow mounts its host. The C# side owns the window + navigation;
// the UI side owns the page contents.
public partial class ClipboardViewerWindow : Window
{
    // Rich vs mini density widths, tuned for a 13" work area. Height is capped to ~70% of the work
    // area at open time so a long history scrolls inside the popup rather than overflowing the screen.
    private const double RichWidth = 316;
    private const double MiniWidth = 240;
    private const double MaxWorkAreaHeightFraction = 0.70;

    private IWebHost? _webHost;

    // The foreground window that was active when the viewer was opened (the user's real paste target).
    // Captured on Open BEFORE the viewer steals focus; re-asserted on close so focus returns there and
    // the sink targets the right window when it synthesizes Ctrl+V.
    private IntPtr _priorForeground;

    // The captured paste target, surfaced for the engine's paste path. The viewer itself is the
    // foreground window while it is open, so a paste that captured the foreground at paste time would
    // target the viewer (and the Ctrl+V would vanish); this is the HWND the user actually wants the
    // paste to land in. IntPtr.Zero when nothing was captured.
    public IntPtr PriorForeground => _priorForeground;

    public ClipboardViewerWindow()
    {
        InitializeComponent();

        // Esc closes (hides) the viewer. KeyDown on the window catches it before the WebView2 child in
        // the common case; the UI page also forwards Esc through the bridge (closeClipboardViewer) as a
        // belt-and-braces path when the WebView has keyboard focus.
        KeyDown += OnKeyDown;

        // Focus loss dismisses the popup, matching the OS quick-pickers (Win+V etc.). Guarded by
        // _suppressDeactivate so a programmatic hide during paste does not double-fire.
        Deactivated += OnDeactivated;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Mounts the web host that renders ui/clipboard-viewer.html. Called once by the composition root
    // after the window is constructed; mirrors MainWindow.AttachWebHost (mount the Control, Load()).
    public void AttachWebHost(IWebHost webHost)
    {
        _webHost = webHost ?? throw new ArgumentNullException(nameof(webHost));

        var hostPanel = this.FindControl<Panel>("HostPanel");
        if (webHost is Control control && hostPanel != null)
        {
            hostPanel.Children.Clear();
            control.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            control.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
            hostPanel.Children.Add(control);
        }

        webHost.Load();
    }

    // Toggles the viewer. If already visible, this is a second hotkey press → hide it. Otherwise
    // capture the current foreground window (the paste target), size for the density, center on the
    // work area, then show focused. Marshals onto the UI thread (the hotkey fires off a Win32 pump).
    public void Toggle(string density)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible)
            {
                Dismiss();
                return;
            }
            Open(density);
        });
    }

    // Captures the foreground window, sizes/centers the popup and shows it focused. Public so the
    // composition root can also open it directly (e.g. from a tray action) if ever needed.
    public void Open(string density)
    {
        // Capture BEFORE we show: showing the viewer makes IT the foreground window, so the only
        // chance to record the user's real target is now.
        _priorForeground = OperatingSystem.IsWindows() ? GetForegroundWindowSafe() : IntPtr.Zero;

        Width = string.Equals(density, "mini", StringComparison.OrdinalIgnoreCase) ? MiniWidth : RichWidth;
        ClampHeightToWorkArea();
        CenterOnWorkArea();

        // Suppress the spurious Deactivated that fires during the initial Show()/Activate() focus
        // transfer (notably as the embedded WebView2 grabs focus). Without this, OnDeactivated ->
        // Dismiss() hides the popup the instant it appears, so the user sees nothing open. Cleared on
        // a later dispatcher frame so a genuine later focus loss still dismisses the viewer.
        _suppressDeactivate = true;
        Show();
        Activate();

        // Activate() focuses the WINDOW, not the embedded WebView2 content — without an explicit
        // hand-off the page's key handlers (Arrow/Enter/Esc) stay dead until the user clicks inside
        // the card. Move keyboard focus into the web content so the hotkey → arrows → Enter flow
        // works immediately.
        _webHost?.FocusContent();

        Dispatcher.UIThread.Post(() => _suppressDeactivate = false, DispatcherPriority.Background);
    }

    // Hides the viewer and restores the previously-focused window so the user lands back where they
    // were (and so a paste targets the right window). Idempotent: a no-op when already hidden.
    public void Dismiss()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsVisible)
                return;

            _suppressDeactivate = true;
            try
            {
                Hide();
                if (OperatingSystem.IsWindows() && _priorForeground != IntPtr.Zero)
                    SetForegroundWindowSafe(_priorForeground);
            }
            finally
            {
                _suppressDeactivate = false;
            }
        });
    }

    private bool _suppressDeactivate;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Dismiss();
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_suppressDeactivate)
            return;
        Dismiss();
    }

    // Set just before a real teardown (app Exit) so OnClosing lets the window close instead of
    // cancelling and hiding it.
    private bool _allowClose;

    // Closing the viewer never quits the app and never destroys the window during normal use: cancel
    // the close and hide instead, so the embedded WebView2 + bridge survive for the next hotkey press.
    // App shutdown calls AllowCloseAndClose() to actually tear it down.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Dismiss();
        }
        base.OnClosing(e);
    }

    // Real teardown at app Exit: allow the close to proceed and close the window (disposing the
    // embedded WebView2 host). Safe to call on the UI thread.
    public void AllowCloseAndClose()
    {
        _allowClose = true;
        Close();
    }

    // Centers the popup on the screen's WORKING area (excluding the taskbar) for the screen the window
    // is currently associated with, falling back to the primary screen.
    private void CenterOnWorkArea()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
            return;

        var wa = screen.WorkingArea;
        var scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;

        // Width/Height are logical; WorkingArea is physical pixels. Convert the logical size to
        // physical to compute a centered physical position.
        var physW = Width * scale;
        var physH = Height * scale;
        var x = wa.X + (wa.Width - physW) / 2;
        var y = wa.Y + (wa.Height - physH) / 2;
        Position = new Avalonia.PixelPoint((int)Math.Round(x), (int)Math.Round(y));
    }

    // Caps the popup height to a fraction of the work area so a long history never overflows the
    // screen (it scrolls inside the WebView instead).
    private void ClampHeightToWorkArea()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null)
            return;

        var scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
        var maxLogicalHeight = (screen.WorkingArea.Height / scale) * MaxWorkAreaHeightFraction;
        if (Height > maxLogicalHeight)
            Height = Math.Max(240, maxLogicalHeight);
    }

    private static IntPtr GetForegroundWindowSafe()
    {
        try { return Platform.Clipboard.Win32.GetForegroundWindow(); }
        catch { return IntPtr.Zero; }
    }

    private static void SetForegroundWindowSafe(IntPtr hwnd)
    {
        try { Platform.Clipboard.Win32.SetForegroundWindow(hwnd); }
        catch { /* best-effort focus restore */ }
    }
}
