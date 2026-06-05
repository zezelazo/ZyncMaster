using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ZyncMaster.App.Bridge;
using ZyncMaster.App.Configuration;
using ZyncMaster.App.State;
using ZyncMaster.App.Tray;
using ZyncMaster.App.Windows;
using ZyncMaster.Core;
using ZyncMaster.Engine;

namespace ZyncMaster.App;

// Composition root. The app is tray-resident: ShutdownMode is OnExplicitShutdown and
// no MainWindow is set on the lifetime, so the process stays alive behind the tray
// icon even when the window is hidden. Quit is driven exclusively from the tray menu.
public partial class App : Application
{
    private TrayController? _tray;
    private IWebHost? _webHost;
    private EngineHost? _engineHost;
    private UiBridge? _bridge;
    private MainWindow? _mainWindow;
    private RegisteredWaitHandle? _showWindowWait;
    private readonly CancellationTokenSource _shutdown = new();

    // FIX G — the long-running background loops are kept in fields so Exit can cancel AND drain them
    // (Task.WhenAll with a bounded wait) before disposing the EngineHost / HttpClient. Without this,
    // a fire-and-forget loop could still be mid-request against a disposed HttpClient at shutdown.
    private Task? _schedulerTask;
    private Task? _statusTask;
    private Task? _heartbeatTask;

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
            _tray.Show();

            // Build the engine and wire the bridge. If settings are missing/invalid the
            // app still runs (tray + web panel) so the user can configure it; the engine
            // pieces just stay null until a valid config is saved and the app relaunched.
            TryWireEngine();

            // --silent (used by login auto-start): stay in the tray, never surface a window.
            var silent = IsSilentLaunch(desktop.Args);

            // Surface the window on startup so the app is visible the moment it launches
            // (it previously stayed hidden behind the tray icon, which read as "didn't
            // start"). With --silent (login auto-start) we deliberately stay tray-only.
            if (!silent)
                Dispatcher.UIThread.Post(() => CreateWindow().Show());

            // Single-instance hand-off: a second launch signals this (owning) instance to
            // surface its window instead of starting a second app. Wait on the named event
            // off-thread and marshal the show onto the UI thread.
            var signal = Program.ShowWindowSignal;
            if (signal != null)
            {
                _showWindowWait = ThreadPool.RegisterWaitForSingleObject(
                    signal,
                    (_, _) => Dispatcher.UIThread.Post(() => CreateWindow().ShowToFront()),
                    state: null,
                    millisecondsTimeOutInterval: Timeout.Infinite,
                    executeOnlyOnce: false);
            }

            desktop.Exit += (_, _) =>
            {
                _shutdown.Cancel();

                // FIX G — drain the background loops before disposing the host/HttpClient so an
                // in-flight request can never hit a disposed HttpClient (ObjectDisposedException) or
                // leave a truncated request. Bounded wait so a wedged loop cannot hang Exit; any
                // task still running past the timeout is abandoned (the process is exiting anyway).
                DrainBackgroundLoops(TimeSpan.FromSeconds(5));

                _showWindowWait?.Unregister(null);
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

        // Build the live engine if it's configured; otherwise fall back to a set of actions
        // that still let the UI work (status, saving the config, window controls) without
        // hanging. The bridge is ALWAYS created so the window chrome and requests respond.
        IEngineActions actions;
        try
        {
            _engineHost = EngineHost.Create(ShowSaveTxtDialogAsync, HostExePath());
            actions = _engineHost.Actions;
        }
        catch (SettingsValidationException)
        {
            actions = MakeUnconfiguredActions();
        }
        catch (SettingsLoadException)
        {
            actions = MakeUnconfiguredActions();
        }

        var transport = new WebViewBridgeTransport((IBridgeTransport)_webHost);
        _bridge = new UiBridge(transport, actions, () => _mainWindow);

        // Auto-sync (the background scheduler + tray sync/pause) only runs once configured.
        if (_engineHost == null)
            return;

        _tray!.SyncNowRequested += () => _ = SafeSyncNow();
        _tray!.PauseToggled += paused => _ = _engineHost.Actions.SetPausedAsync(paused, _shutdown.Token);

        // Multi-pair scheduler replaces the single SyncLoop: it drives every configured pair
        // on its own cadence. After each tick we refresh status and push it to the UI + tray.
        var scheduler = _engineHost.Scheduler;

        // FIX C — the device-lease heartbeat runs independently of the scheduler's startup delay so
        // the lease is renewed promptly even before the first (delayed) sync tick. Tracked in a
        // field so Exit can drain it.
        _heartbeatTask = Task.Run(() => _engineHost.HeartbeatLoop.RunAsync(_shutdown.Token));

        // Delay the first tick so the heavy Outlook COM read + push does not compete with
        // app/window startup (it would otherwise spike CPU/IO the moment the app opens). Tracked in
        // a field (FIX G) so Exit cancels AND drains it before disposing the host.
        _schedulerTask = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), _shutdown.Token); }
            catch (OperationCanceledException) { return; }

            // Push status to the UI/tray after each scheduler tick by polling the engine's
            // recorded status on the same cadence as the scheduler runs in the background.
            _statusTask = Task.Run(() => PublishStatusLoopAsync(_shutdown.Token));

            try
            {
                await scheduler.RunAsync(_shutdown.Token);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                // The scheduler should never throw out (per-pair failures are isolated), but
                // if it does, surface it as a status message instead of a silent dead loop.
                _bridge?.PushStatus(new AppStatus { Status = SyncStatus.Error, LastMessage = $"Sync scheduler stopped: {ex.Message}" });
            }
        });
    }

    // FIX G — cancel-then-drain the tracked background loops with a bounded wait. The CTS is already
    // cancelled by the caller; here we WAIT for each loop to observe the cancellation and unwind
    // (closing its in-flight HTTP request) before the host/HttpClient are disposed. Tasks that do
    // not finish within the timeout are abandoned — the process is exiting, so a hung loop must not
    // block shutdown. All exceptions (the loops complete with OperationCanceledException) are
    // swallowed: this runs on the Exit path where throwing would be worse than a noisy log.
    private void DrainBackgroundLoops(TimeSpan timeout)
    {
        var tasks = new[] { _schedulerTask, _statusTask, _heartbeatTask };
        var pending = Array.FindAll(tasks, t => t is not null)!;
        if (pending.Length == 0)
            return;

        try
        {
            Task.WhenAll(pending!).Wait(timeout);
        }
        catch
        {
            // Loops unwind via OperationCanceledException; a timeout or fault here is non-fatal at
            // Exit. The bounded Wait guarantees shutdown proceeds regardless.
        }
    }

    // Mirrors the scheduler's heartbeat onto the UI/tray. The scheduler itself has no
    // status callback, so we periodically read EngineActions' status snapshot and push it.
    private async Task PublishStatusLoopAsync(CancellationToken ct)
    {
        if (_engineHost == null || _bridge == null)
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var status = await _engineHost.Actions.GetStatusAsync(ct);
                _bridge.PushStatus(status);
                Dispatch(() => _tray?.SetStatus(status.Status));
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown — exit cleanly.
        }
        catch
        {
            // Never let status publishing crash the app.
        }
    }

    // The host executable path registered for login auto-start.
    private static string HostExePath()
    {
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
            return exe;
        return Assembly.GetExecutingAssembly().Location;
    }

    private static bool IsSilentLaunch(string[]? args)
    {
        if (args == null)
            return false;
        foreach (var a in args)
            if (string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // Shows a native Save-As dialog for the basic .txt export and returns the chosen path,
    // or null if the user cancelled. Runs on the UI thread; creates a window to host the
    // picker if one is not already open (the app is tray-resident).
    private Task<string?> ShowSaveTxtDialogAsync(string suggestedName)
    {
        var tcs = new TaskCompletionSource<string?>();

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var window = _mainWindow ?? CreateWindow();
                var provider = window.StorageProvider;
                if (provider == null || !provider.CanSave)
                {
                    tcs.TrySetResult(null);
                    return;
                }

                var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Export calendar to .txt",
                    SuggestedFileName = suggestedName,
                    DefaultExtension = "txt",
                    FileTypeChoices = new[]
                    {
                        new FilePickerFileType("Text file") { Patterns = new[] { "*.txt" } },
                    },
                });

                tcs.TrySetResult(file?.TryGetLocalPath());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return tcs.Task;
    }

    private static UnconfiguredEngineActions MakeUnconfiguredActions()
    {
        var exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
                     ?? Directory.GetCurrentDirectory();
        var settingsPath = Path.Combine(exeDir, "settings.json");
        var repo = new SettingsRepository<AppSettings>(new PhysicalFileSystem());
        return new UnconfiguredEngineActions(repo, settingsPath);
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

    // Returns the single dashboard window, creating + wiring it on first use. The window is
    // never destroyed (closing only hides it to the tray), so a startup auto-show and a later
    // tray "Open" share the same instance — avoiding a second window / a second WebView2.
    private MainWindow CreateWindow()
    {
        if (_mainWindow != null)
            return _mainWindow;

        var window = new MainWindow();
        _mainWindow = window;
        if (_webHost != null)
            window.AttachWebHost(_webHost, OpenWebPanelInBrowser);
        return window;
    }

    private void OpenWebPanelInBrowser()
    {
        // Only meaningful for the loopback host. With the embedded WebView2 the window
        // itself is the panel, opened from the tray's "Open ZyncMaster".
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
