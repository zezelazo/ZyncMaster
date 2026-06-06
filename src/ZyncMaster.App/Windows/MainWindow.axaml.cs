using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ZyncMaster.App.Bridge;

namespace ZyncMaster.App.Windows;

// The dashboard window. Frameless, rounded, transparent. It hosts the web UI through
// an IWebHost. With the loopback WebHost there is no embedded WebView control, so the
// window shows the loopback URL and an "Open web panel" button that launches the UI in
// the default browser; a real WebView implementation would mount its control into
// HostPanel instead. Closing the window does NOT quit the app: it cancels the close and
// hides the window so the process stays resident in the tray.
public partial class MainWindow : Window, IWindowControl
{
    private IWebHost? _webHost;
    private Action? _openPanel;

    public MainWindow()
    {
        InitializeComponent();

        // Frameless, no-top-border chrome is declared in XAML: WindowDecorations="None" +
        // ExtendClientAreaToDecorationsHint="True". On Avalonia 12.0.3 the old
        // ExtendClientAreaChromeHints enum no longer exists on Window (it was removed in the 12.x
        // decorations rework — only ExtendClientAreaToDecorationsHint / *TitleBarHeightHint and the
        // WindowDecorations enum {None,BorderOnly,Full} remain), so there is nothing to set here.

        // When the window is maximized/restored the embedded WebView2 (a native child HWND
        // managed by a NativeControlHost) must be re-laid-out so its CoreWebView2 bounds
        // match the new client size; otherwise the previous-state bounds linger and the
        // content renders at the wrong size ("lost everything" on restore). This handler is
        // also what makes the extend-client-area chrome (MainWindow.axaml Option A) safe: it
        // re-syncs the WebView2 to the new client size on every WindowState change. Avalonia does
        // re-arrange on a state change, but the WindowState transition and the host's
        // ArrangeOverride can race, so we force an extra invalidation on the next UI turn.
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
                Dispatcher.UIThread.Post(InvalidateHostLayout, DispatcherPriority.Loaded);
        };
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    // Forces a measure/arrange pass on the web host control so the embedded WebView2 syncs
    // its native bounds to the current client size (run after a WindowState change).
    private void InvalidateHostLayout()
    {
        var hostPanel = this.FindControl<Panel>("HostPanel");
        if (hostPanel == null)
            return;
        hostPanel.InvalidateMeasure();
        hostPanel.InvalidateArrange();
        foreach (var child in hostPanel.Children)
        {
            child.InvalidateMeasure();
            child.InvalidateArrange();
        }
    }

    // Brings the window to the foreground, restoring it if minimized or hidden. Used by the
    // tray "Open" action and by a second launch attempt (single-instance focus hand-off).
    public void ShowToFront()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Show();
            Activate();
            Topmost = true;
            Topmost = false;
        });
    }

    // Attaches the web host and the "open in browser" action. Called by the composition
    // root after the host has started so the window can surface the loopback URL.
    public void AttachWebHost(IWebHost webHost, Action openPanel)
    {
        _webHost = webHost ?? throw new ArgumentNullException(nameof(webHost));
        _openPanel = openPanel ?? throw new ArgumentNullException(nameof(openPanel));

        // Resolve named controls by tree lookup rather than relying on generated fields:
        // with AvaloniaXamlLoader.Load the x:Name fields are not guaranteed to be populated.
        var hostPanel = this.FindControl<Panel>("HostPanel");

        // An embedded WebView host is itself a Control (the WebView2 host); mount it as
        // the window content and let it render the UI in-window. The loopback host is not
        // a Control, so it falls through to the URL + "open in browser" placeholder.
        if (webHost is Control control)
        {
            if (hostPanel != null)
            {
                hostPanel.Children.Clear();
                control.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
                control.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
                hostPanel.Children.Add(control);
            }

#if WIN_WEBVIEW2
            // FIX 2 — if the embedded WebView2 cannot initialise (most commonly because the Evergreen
            // WebView2 Runtime is not installed), swap the blank host out for a native Avalonia panel
            // that explains the problem and links to the runtime download. Subscribe before Load() and
            // also handle the case where the host already failed (HasFailed) so we never miss it.
            if (webHost is WebView2WebHost wv2)
            {
                wv2.InitializationFailed += () => ShowWebView2MissingPanel(hostPanel);
                if (wv2.HasFailed)
                    ShowWebView2MissingPanel(hostPanel);
            }
#endif

            webHost.Load();
            return;
        }

        if (_webHost is WebHost loopback)
        {
            var urlText = this.FindControl<TextBlock>("HostUrlText");
            if (urlText != null)
                urlText.Text = $"Web panel served on {loopback.BaseUrl}";
        }

        var openButton = this.FindControl<Button>("OpenPanelButton");
        if (openButton != null)
            openButton.Click += (_, _) => _openPanel?.Invoke();
    }

    // Set just before a real shutdown (tray "Quit") so OnClosing lets the window close
    // instead of cancelling and hiding it. Note: Shutdown() forces teardown and ignores the
    // OnClosing cancel regardless, so this is defensive — it keeps Quit working if the exit
    // path is ever switched to the cancel-respecting TryShutdown().
    private bool _allowClose;

    // Allows the next close to proceed (used by the tray Quit path through the lifetime).
    public void AllowClose() => _allowClose = true;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Closing hides to the tray, never quits — the X on the custom title bar (bridge
        // Close) and the OS-level close both land here, keeping the process resident so
        // background sync keeps running. A real Quit sets _allowClose first.
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

#if WIN_WEBVIEW2
    // Replaces the blank WebView2 host with a native Avalonia panel explaining that the Evergreen
    // WebView2 Runtime is missing, plus a button that opens the download page in the system browser.
    // Runs on the UI thread (InitializationFailed is posted there); idempotent because it rebuilds
    // the host panel's children from scratch.
    private void ShowWebView2MissingPanel(Panel? hostPanel)
    {
        hostPanel ??= this.FindControl<Panel>("HostPanel");
        if (hostPanel == null)
            return;

        var heading = new TextBlock
        {
            Text = "WebView2 Runtime no instalado",
            Foreground = Avalonia.Media.Brushes.White,
            FontSize = 20,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = Avalonia.Media.TextAlignment.Center,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };

        var body = new TextBlock
        {
            Text = "Zync Master necesita el runtime de Microsoft Edge WebView2 para mostrar su "
                 + "interfaz. Instálalo y vuelve a abrir la aplicación.",
            Foreground = Avalonia.Media.Brush.Parse("#9FB0CC"),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = Avalonia.Media.TextAlignment.Center,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MaxWidth = 360,
        };

        var button = new Button
        {
            Content = "Descargar WebView2 Runtime",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        button.Click += (_, _) => OpenInSystemBrowser(WebView2WebHost.RuntimeDownloadUrl);

        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 12,
        };
        stack.Children.Add(heading);
        stack.Children.Add(body);
        stack.Children.Add(button);

        hostPanel.Children.Clear();
        hostPanel.Children.Add(stack);
    }

    // Opens a URL in the system's default browser. Best-effort: a missing/blocked browser must not
    // crash the window. Restricted to http/https so a non-web scheme can never be shelled out.
    private static void OpenInSystemBrowser(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri)
            {
                UseShellExecute = true,
            });
        }
        catch { /* no browser / blocked: swallow */ }
    }
#endif

    // ---- IWindowControl: driven by the web title bar through the bridge ----
    // The window is frameless (extend-client-area + NoChrome: resizable WM frame, no painted
    // title bar), so these provide minimize / maximize / close / move. All marshal to the UI thread.

    public void Minimize() => Dispatcher.UIThread.Post(() => WindowState = WindowState.Minimized);

    public void ToggleMaximize() => Dispatcher.UIThread.Post(() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);

    void IWindowControl.Close() => Dispatcher.UIThread.Post(Hide); // hide to tray, do not quit

    public void BeginDragMove() => Dispatcher.UIThread.Post(() =>
    {
        // Primary drag is handled by WebView2's non-client region support (CSS app-region:
        // drag). This is a fallback for Windows using the classic caption-move message.
        if (!OperatingSystem.IsWindows()) return;
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        ReleaseCapture();
        SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
    });

    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
