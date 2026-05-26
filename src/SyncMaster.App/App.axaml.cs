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

            // The window is created lazily on first open so startup stays fast and
            // the app can sit in the tray without a visible window.
            _tray = new TrayController(desktop, () => new MainWindow());
            _tray.Show();

            desktop.Exit += (_, _) => _tray?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
