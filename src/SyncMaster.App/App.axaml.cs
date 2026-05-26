using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SyncMaster.App.Bridge;
using SyncMaster.App.Configuration;
using SyncMaster.App.State;
using SyncMaster.App.Tray;
using SyncMaster.App.Windows;
using SyncMaster.Core;
using SyncMaster.Engine;

namespace SyncMaster.App;

// Composition root. The app is tray-resident: ShutdownMode is OnExplicitShutdown and
// no MainWindow is set on the lifetime, so the process stays alive behind the tray
// icon even when the window is hidden. Quit is driven exclusively from the tray menu.
public partial class App : Application
{
    private TrayController? _tray;
    private IWebHost? _webHost;
    private EngineHost? _engineHost;
    private UiBridge? _bridge;
    private readonly CancellationTokenSource _shutdown = new();

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

            // Pick the web host: an embedded WebView2 on Windows (renders the UI inside
            // the window and carries the bridge over window.chrome.webview), otherwise the
            // loopback host (serves the bundled UI + an "open in browser" action).
#if WIN_WEBVIEW2
            if (OperatingSystem.IsWindows())
            {
                _webHost = new WebView2WebHost(); // navigates once mounted into the window
            }
            else
#endif
            {
                var loopback = new WebHost();
                loopback.Load();
                _webHost = loopback;
            }

            _tray = new TrayController(desktop, CreateWindow);
            _tray.OpenWebPanelRequested += OpenWebPanelInBrowser;
            _tray.Show();

            // Build the engine and wire the bridge. If settings are missing/invalid the
            // app still runs (tray + web panel) so the user can configure it; the engine
            // pieces just stay null until a valid config is saved and the app relaunched.
            TryWireEngine();

            // Debug/smoke aid: open the window on startup so the window + WebView2 path can
            // be exercised without a tray click. Off unless SYNCMASTER_AUTOSHOW=1.
            if (Environment.GetEnvironmentVariable("SYNCMASTER_AUTOSHOW") == "1")
                Avalonia.Threading.Dispatcher.UIThread.Post(() => CreateWindow().Show());

            desktop.Exit += (_, _) =>
            {
                _shutdown.Cancel();
                _tray?.Dispose();
                _bridge = null;
                _engineHost?.Dispose();
                (_webHost as IDisposable)?.Dispose();
                _shutdown.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void TryWireEngine()
    {
        if (_webHost == null)
            return;

        try
        {
            _engineHost = EngineHost.Create();
        }
        catch (SettingsValidationException)
        {
            // Required serverBaseUrl not set yet — leave the engine unwired so the user
            // can configure it from the panel.
            return;
        }
        catch (SettingsLoadException)
        {
            // settings.json exists but is corrupt — same handling.
            return;
        }

        var transport = new WebViewBridgeTransport((IBridgeTransport)_webHost);
        _bridge = new UiBridge(transport, _engineHost.Actions);

        // Tray "Sync now" / "Pause" route through the engine too.
        _tray!.SyncNowRequested += () => _ = SafeSyncNow();
        _tray!.PauseToggled += paused => _ = _engineHost.Actions.SetPausedAsync(paused, _shutdown.Token);

        // Drive a sync loop in the background; each cycle records its result, pushes the
        // status to the web layer, and updates the tray icon.
        var cycle = new StatusPushingCycle(
            _engineHost.SyncCycle,
            _engineHost.Actions,
            _bridge,
            status => Dispatch(() => _tray?.SetStatus(status)));

        var interval = TimeSpan.FromMinutes(_engineHost.Settings.IntervalMinutes);
        var loop = new SyncLoop(cycle, interval);
        _ = Task.Run(() => loop.RunAsync(_shutdown.Token));
    }

    private async Task SafeSyncNow()
    {
        if (_engineHost == null || _bridge == null)
            return;
        try
        {
            var result = await _engineHost.Actions.SyncNowAsync(_shutdown.Token);
            _engineHost.Actions.RecordResult(result);
            var status = await _engineHost.Actions.GetStatusAsync(_shutdown.Token);
            _bridge.PushStatus(status);
            Dispatch(() => _tray?.SetStatus(status.Status));
        }
        catch
        {
            // Manual sync failures surface as the next status push; never crash the app.
        }
    }

    private static void Dispatch(Action action)
        => Avalonia.Threading.Dispatcher.UIThread.Post(action);

    private MainWindow CreateWindow()
    {
        var window = new MainWindow();
        if (_webHost != null)
            window.AttachWebHost(_webHost, OpenWebPanelInBrowser);
        return window;
    }

    private void OpenWebPanelInBrowser()
    {
        // Only meaningful for the loopback host. With the embedded WebView2 the window
        // itself is the panel, opened from the tray's "Open SyncMaster".
        if (_webHost is not WebHost loopback)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(loopback.BaseUrl) { UseShellExecute = true });
        }
        catch
        {
            // Best effort: if the default browser can't be launched there is nothing
            // useful to surface from the tray click.
        }
    }
}
