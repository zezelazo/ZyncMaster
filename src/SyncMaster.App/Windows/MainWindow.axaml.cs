using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SyncMaster.App.Windows;

// The dashboard window. Frameless, rounded, transparent. It hosts the web UI through
// an IWebHost. With the loopback WebHost there is no embedded WebView control, so the
// window shows the loopback URL and an "Open web panel" button that launches the UI in
// the default browser; a real WebView implementation would mount its control into
// HostPanel instead. Closing the window does NOT quit the app: it cancels the close and
// hides the window so the process stays resident in the tray.
public partial class MainWindow : Window
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

        if (_webHost is WebHost loopback)
            HostUrlText.Text = $"Web panel served on {loopback.BaseUrl}";

        OpenPanelButton.Click += (_, _) => _openPanel?.Invoke();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Closing hides, never quits: keep the tray-resident process alive.
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }
}
