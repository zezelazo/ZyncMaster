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
// (focus loss). "Close" here means HIDE — the window instance is reused across hotkey presses.
//
// This is a NATIVE Avalonia window — NOT a WebView2 host. A child WebView2 HWND composites opaque, so
// an acrylic translucent popup is impossible with it (every prior attempt failed). The translucency
// comes from a DWM system backdrop (DWMSBT_TRANSIENTWINDOW) applied in OnOpened, with a faint Card
// veil as the fallback on older Windows. The rows are plain Avalonia controls the composition root
// fills via SetRows; PasteRequested/DeleteRequested surface the user's choice back to the host.
public partial class ClipboardViewerWindow : Window
{
    // Rich vs mini density widths, tuned for a 13" work area. Height is capped to ~70% of the work
    // area at open time so a long history scrolls inside the popup rather than overflowing the screen.
    private const double RichWidth = 316;
    private const double MiniWidth = 240;
    private const double MaxWorkAreaHeightFraction = 0.70;

    // The foreground window that was active when the viewer was opened (the user's real paste target).
    // Captured on Open BEFORE the viewer steals focus; re-asserted on close so focus returns there and
    // the sink targets the right window when it synthesizes Ctrl+V.
    private IntPtr _priorForeground;

    // The captured paste target, surfaced for the engine's paste path. The viewer itself is the
    // foreground window while it is open, so a paste that captured the foreground at paste time would
    // target the viewer (and the Ctrl+V would vanish); this is the HWND the user actually wants the
    // paste to land in. IntPtr.Zero when nothing was captured.
    public IntPtr PriorForeground => _priorForeground;

    // Raised when the user chose to paste a row (Enter / double-tap). The host resolves the entry and
    // synthesizes the OS paste into the captured foreground window. Carries the chosen row.
    public event Action<ClipboardRow>? PasteRequested;

    // Raised when the user chose to remove a row (Del / trash button). The host deletes it server-side
    // and refreshes the list. Carries the chosen row.
    public event Action<ClipboardRow>? DeleteRequested;

    public ClipboardViewerWindow()
    {
        InitializeComponent();
        var list = this.FindControl<ListBox>("List")!;
        list.DoubleTapped += (_, _) => RequestPasteSelected();

        // Esc closes (hides) the viewer; Enter pastes the selection; Del removes it. KeyDown on the
        // window catches them — the list owns keyboard focus while the popup is open.
        KeyDown += OnKeyDown;

        // Focus loss dismisses the popup, matching the OS quick-pickers (Win+V etc.). Guarded by
        // _suppressDeactivate so a programmatic hide during paste does not double-fire.
        Deactivated += OnDeactivated;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Applies the DWM system backdrop (the acrylic-like transient material) and rounded corners once
    // the native HWND exists. Windows-only; an older OS / a failed call simply keeps the Card veil.
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero) return;
            int backdrop = Platform.Clipboard.Win32.DWMSBT_TRANSIENTWINDOW;
            Platform.Clipboard.Win32.DwmSetWindowAttribute(
                hwnd, Platform.Clipboard.Win32.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
            int corner = Platform.Clipboard.Win32.DWMWCP_ROUND;
            Platform.Clipboard.Win32.DwmSetWindowAttribute(
                hwnd, Platform.Clipboard.Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        }
        catch { /* old OS: keep the Card veil */ }
    }

    // Replaces the list with a fresh snapshot; selection always resets to the first (newest) row.
    public void SetRows(System.Collections.Generic.IReadOnlyList<ClipboardRow> rows)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var list = this.FindControl<ListBox>("List")!;
            list.ItemTemplate = BuildRowTemplate();
            list.ItemsSource = rows;
            list.SelectedIndex = rows.Count > 0 ? 0 : -1;
            var h = this.FindControl<TextBlock>("HeaderText");
            if (h != null) h.Text = $"CLIPBOARD · {rows.Count} ITEM{(rows.Count == 1 ? "" : "S")}";
            if (rows.Count > 0) Dispatcher.UIThread.Post(() => list.ScrollIntoView(0));
        });
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

        // Activate() focuses the WINDOW, not the list — without an explicit hand-off the key handlers
        // (Arrow/Enter/Del/Esc) stay dead until the user clicks inside the card. Move keyboard focus
        // into the list so the hotkey → arrows → Enter flow works immediately.
        this.FindControl<ListBox>("List")?.Focus();

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

    private Avalonia.Controls.Templates.IDataTemplate BuildRowTemplate()
    {
        return new Avalonia.Controls.Templates.FuncDataTemplate<ClipboardRow>((row, _) =>
        {
            var title = new TextBlock
            {
                Text = row.Title, Foreground = Avalonia.Media.Brushes.White, FontSize = 13,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            };
            var meta = new TextBlock
            {
                Text = row.Meta,
                Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#8AA0C0")),
                FontSize = 11,
            };
            var stack = new StackPanel { Spacing = 2, Children = { title, meta } };

            var trash = new Button
            {
                Content = "🗑", Padding = new Avalonia.Thickness(6, 2),
                Background = Avalonia.Media.Brushes.Transparent, BorderThickness = new Avalonia.Thickness(0),
                Opacity = 0, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            trash.Click += (_, e) => { e.Handled = true; DeleteRequested?.Invoke(row); };

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Avalonia.Thickness(2),
            };
            Grid.SetColumn(stack, 0);
            Grid.SetColumn(trash, 1);
            grid.Children.Add(stack);
            grid.Children.Add(trash);

            var border = new Border
            {
                CornerRadius = new Avalonia.CornerRadius(8), Padding = new Avalonia.Thickness(10, 8),
                Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#14FFFFFF")),
                Child = grid,
            };
            border.PointerEntered += (_, _) => trash.Opacity = 1;
            border.PointerExited += (_, _) => trash.Opacity = 0;
            return border;
        });
    }

    private void RequestPasteSelected()
    {
        if (this.FindControl<ListBox>("List")?.SelectedItem is ClipboardRow row)
            PasteRequested?.Invoke(row);
    }

    private void RequestDeleteSelected()
    {
        if (this.FindControl<ListBox>("List")?.SelectedItem is ClipboardRow row)
            DeleteRequested?.Invoke(row);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: e.Handled = true; Dismiss(); break;
            case Key.Enter:  e.Handled = true; RequestPasteSelected(); break;
            case Key.Delete: e.Handled = true; RequestDeleteSelected(); break;
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
