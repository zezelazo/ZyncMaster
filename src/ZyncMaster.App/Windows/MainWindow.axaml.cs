using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia.Controls;
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
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
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

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Closing hides, never quits: keep the tray-resident process alive.
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    // ---- IWindowControl: driven by the web title bar through the bridge ----
    // The window is frameless (BorderOnly: resizable border, no OS title bar), so these
    // provide minimize / maximize / close / move. All marshal to the UI thread.

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
