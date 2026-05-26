using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SyncMaster.App.Tray;
using SyncMaster.App.Windows;

namespace SyncMaster.App;

// Composition root. The app is tray-resident: ShutdownMode is OnExplicitShutdown and
// no MainWindow is set on the lifetime, so the process stays alive behind the tray
// icon even when the window is hidden. Quit is driven exclusively from the tray menu.
public partial class App : Application
{
    private TrayController? _tray;
    private WebHost? _webHost;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Tray-resident: never quit just because the last window closed.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Start the loopback web host that serves the bundled UI and carries the bridge.
            _webHost = new WebHost();
            _webHost.Load();

            // The window is created lazily on first open so startup stays fast and the
            // app can sit in the tray without a visible window.
            _tray = new TrayController(desktop, CreateWindow);
            _tray.OpenWebPanelRequested += OpenWebPanelInBrowser;
            _tray.Show();

            desktop.Exit += (_, _) =>
            {
                _tray?.Dispose();
                _webHost?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private MainWindow CreateWindow()
    {
        var window = new MainWindow();
        if (_webHost != null)
            window.AttachWebHost(_webHost, OpenWebPanelInBrowser);
        return window;
    }

    private void OpenWebPanelInBrowser()
    {
        if (_webHost == null)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(_webHost.BaseUrl) { UseShellExecute = true });
        }
        catch
        {
            // Best effort: if the default browser can't be launched there is nothing
            // useful to surface from the tray click.
        }
    }
}
