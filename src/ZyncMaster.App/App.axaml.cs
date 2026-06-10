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

    // Clipboard quick-viewer (Plan 3 Task 6): its own frameless top-most window with its own WebView2
    // host + bridge over the SHARED EngineActions. Created lazily on first hotkey press so the second
    // WebView2 is not spun up on machines/sessions that never use the clipboard.
    private ClipboardViewerWindow? _clipboardViewer;
    private UiBridge? _clipboardViewerBridge;
    private IWebHost? _clipboardViewerHost;
    private Task? _clipboardTask;

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

            // --verbose (or ZYNCMASTER_VERBOSE=1): lower the local log level to Debug. Parsed here
            // off desktop.Args (the same place --silent is read) and passed into EngineHost.Create.
            var verbose = IsVerboseLaunch(desktop.Args);

            // Build the engine and wire the bridge. If settings are missing/invalid the
            // app still runs (tray + web panel) so the user can configure it; the engine
            // pieces just stay null until a valid config is saved and the app relaunched.
            TryWireEngine(verbose);

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

                // Stop the clipboard pipeline before the host/HttpClient go away: unregister the global
                // hotkey, stop OS capture, and tear down the viewer window + its WebView2 host.
                StopClipboard();

                _showWindowWait?.Unregister(null);
                _tray?.Dispose();
                _bridge = null;
                _clipboardViewerBridge = null;
                _engineHost?.Dispose();
                (_webHost as IDisposable)?.Dispose();
                _shutdown.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void TryWireEngine(bool verbose)
    {
        if (_webHost == null)
            return;

        // Build the live engine if it's configured; otherwise fall back to a set of actions
        // that still let the UI work (status, saving the config, window controls) without
        // hanging. The bridge is ALWAYS created so the window chrome and requests respond.
        IEngineActions actions;
        try
        {
            _engineHost = EngineHost.Create(ShowSaveTxtDialogAsync, HostExePath(), verbose);
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
        // FIX 1 — a read-only install location (settings.json could not be created/written) must not
        // crash the app on first launch. Degrade to the unconfigured actions so the tray + web panel
        // still come up and the user can see what is wrong. The new settings path lives under
        // %LOCALAPPDATA% so this should be rare, but a locked file / odd ACL can still surface here.
        catch (IOException)
        {
            actions = MakeUnconfiguredActions();
        }
        catch (UnauthorizedAccessException)
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

        // Boot-time device auto-registration: if the user is ALREADY signed in when the app opens
        // (the common "already logged in" case), register the device up front so it has an api key
        // before the heartbeat/scheduler tick — otherwise both no-op silently with an empty key and
        // nothing ever syncs until a manual "Sync now". Best-effort: EnsureDeviceRegisteredAsync is a
        // no-op when there is no identity or the key already exists, and swallows transient failures.
        var bootEngine = _engineHost;
        _ = Task.Run(async () =>
        {
            try
            {
                await bootEngine.Actions.EnsureDeviceRegisteredAsync(_shutdown.Token);
            }
            catch (OperationCanceledException) { /* shutdown */ }
            catch (Exception ex)
            {
                bootEngine.Logger.Log(LogLevel.Warning, "Boot-time device registration failed.", ex);
            }
        });

        // Clipboard module boot: wire the viewer-close callback + the live "clipboard:item" push, then
        // start the capture/transport/hotkey pipeline once the device is registered. Kept in its own
        // task so it never blocks the scheduler/heartbeat startup. Tracked so Exit can drain it.
        bootEngine.Actions.CloseClipboardViewer = () => _clipboardViewer?.Dismiss();
        // The paste target: the foreground window the viewer captured BEFORE it opened. At paste time
        // the viewer itself is the foreground window, so the sink must aim the synthetic Ctrl+V at
        // this captured handle — capturing the foreground when the paste runs would target the viewer
        // and the keystroke would land nowhere.
        bootEngine.Actions.PasteTargetWindowProvider = () => _clipboardViewer?.PriorForeground ?? IntPtr.Zero;
        bootEngine.ClipboardHotkey.Pressed += OnClipboardHotkeyPressed;
        bootEngine.ClipboardTransport.ItemReceived += OnClipboardItemReceived;
        // The server broadcast excludes the origin device (no echo), so a successful LOCAL publish is
        // the only signal that this machine's own copy exists: mirror it into the open UI lists
        // through the same "clipboard:item" push the receive path uses.
        bootEngine.ClipboardService.ItemPublished += OnClipboardItemPublished;
        // Live roster + per-device settings: push to the MAIN window bridge (the clipboard devices /
        // settings screen lives in the dashboard, not the viewer) so an open screen refreshes its online
        // dots, "(N online)" count and the per-device send/receive toggles across the user's windows.
        bootEngine.Actions.ClipboardPresenceChanged += OnClipboardPresenceChanged;
        bootEngine.Actions.ClipboardSettingsChanged += OnClipboardSettingsChanged;
        bootEngine.Actions.ClipboardDeleted += OnClipboardDeleted;
        // The E2E text key landed (a peer relayed it): previously undecryptable history rows just
        // became readable, so push a refresh signal to any open history list.
        bootEngine.ClipboardKeyExchange.TextKeyChanged += OnClipboardTextKeyChanged;
        _clipboardTask = Task.Run(() => StartClipboardAsync(bootEngine, _shutdown.Token));

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
                _engineHost?.Logger.Log(LogLevel.Error, "Sync scheduler stopped unexpectedly.", ex);
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
        var tasks = new[] { _schedulerTask, _statusTask, _heartbeatTask, _clipboardTask };
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
        catch (Exception ex)
        {
            // Never let status publishing crash the app.
            _engineHost?.Logger.Log(LogLevel.Warning, "Status publish loop failed.", ex);
        }
    }

    // ---------------- Clipboard module (Plan 2/3 Task 11) ----------------

    // Brings the clipboard pipeline online once the device is registered:
    //   1) wait for a device key (registration completed) so the transport/devices calls authenticate;
    //   2) read THIS device's id/name + its clipboard settings and seed the engine's live state;
    //   3) bootstrap the E2E text key (generate as the first device, else wait for a peer to relay it);
    //   4) connect the live WebSocket, start OS capture, and register the global viewer hotkey.
    // Best-effort throughout: any step failing is logged and never crashes the app — the clipboard is
    // an additive feature and the rest of the App keeps working.
    private async Task StartClipboardAsync(EngineHost engine, CancellationToken ct)
    {
        try
        {
            // 2) Resolve this device + its settings. GetDeviceAsync self-heals/awaits registration via
            //    the engine's WithDeviceKeyAsync, so a key is present by the time it returns.
            var device = await engine.Actions.GetDeviceAsync(ct);
            var settings = await engine.ClipboardTransport.GetSettingsAsync(device.DeviceId, ct);
            engine.Actions.InitializeClipboard(settings, device.DeviceId, device.Name);

            // 3) Text-key bootstrap: empty history => this is the first device, generate + keep the key;
            //    otherwise wait for a peer to relay it (OnKeyReceivedAsync, wired in ClipboardService).
            var history = await engine.ClipboardTransport.GetHistoryAsync(ct);
            var textKey = await engine.ClipboardKeyExchange.EnsureTextKeyAsync(history.Count == 0, ct);

            // 4) Go live.
            await engine.ClipboardTransport.ConnectAsync(ct);
            engine.ClipboardService.Start();
            engine.ClipboardHotkey.Register(settings.ViewerHotkey);

            // 5) Zero-touch key admission. No key -> advertise our need (needsTextKey + our public
            //    key via the settings upsert) so a key-holder relays it; key in hand -> sweep the
            //    roster for peers already waiting. Best-effort: the same flows re-run automatically
            //    on the settings/presence triggers inside ClipboardKeyExchange, so a failure here
            //    only delays admission, never loses it — and must not take the started pipeline down.
            try
            {
                if (textKey is null)
                    await engine.ClipboardKeyExchange.RequestKeyAsync(ct);
                else
                    await engine.ClipboardKeyExchange.AdmitPendingPeersAsync(ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                engine.Logger.Log(LogLevel.Warning,
                    "Clipboard key admission bootstrap failed; it will retry on the live triggers.", ex);
            }

            engine.Logger.Log(LogLevel.Info,
                $"Clipboard module started (device={device.DeviceId}, hotkey={settings.ViewerHotkey}).");
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            // A transient network failure at boot (DNS still down after resume, server mid-deploy)
            // logs one concise line; anything else keeps the full exception for diagnosis.
            var transient = ZyncMaster.Core.TransientNetworkError.Describe(ex);
            if (transient is not null)
                engine.Logger.Log(LogLevel.Warning,
                    $"Clipboard module failed to start ({transient}); it will be unavailable this session.");
            else
                engine.Logger.Log(LogLevel.Warning, "Clipboard module failed to start; it will be unavailable this session.", ex);
        }
    }

    // Tears down the clipboard pipeline at Exit: unsubscribe, stop OS capture, unregister the hotkey,
    // dispose the transport's socket, and close the viewer window + its host. Best-effort and order-
    // independent — any individual failure is swallowed so shutdown always proceeds.
    private void StopClipboard()
    {
        var engine = _engineHost;
        if (engine != null)
        {
            try { engine.ClipboardHotkey.Pressed -= OnClipboardHotkeyPressed; } catch { }
            try { engine.ClipboardTransport.ItemReceived -= OnClipboardItemReceived; } catch { }
            try { engine.ClipboardService.ItemPublished -= OnClipboardItemPublished; } catch { }
            try { engine.Actions.ClipboardPresenceChanged -= OnClipboardPresenceChanged; } catch { }
            try { engine.Actions.ClipboardSettingsChanged -= OnClipboardSettingsChanged; } catch { }
            try { engine.Actions.ClipboardDeleted -= OnClipboardDeleted; } catch { }
            try { engine.ClipboardKeyExchange.TextKeyChanged -= OnClipboardTextKeyChanged; } catch { }
            try { engine.ClipboardService.Stop(); } catch { }
            try { (engine.ClipboardHotkey as IDisposable)?.Dispose(); } catch { }
            try { (engine.ClipboardCapture as IDisposable)?.Dispose(); } catch { }
            try { (engine.ClipboardTransport as IDisposable)?.Dispose(); } catch { }
        }

        try { (_clipboardViewerHost as IDisposable)?.Dispose(); } catch { }
        try
        {
            var viewer = _clipboardViewer;
            if (viewer != null)
                Dispatcher.UIThread.Post(() => { try { viewer.AllowCloseAndClose(); } catch { } });
        }
        catch { }
    }

    // Global hotkey pressed: open (or toggle) the viewer. The viewer captures the foreground window
    // itself on open so a later paste targets the user's real window. Marshalled onto the UI thread.
    private void OnClipboardHotkeyPressed()
    {
        if (_engineHost == null)
            return;
        var density = _engineHost.Actions.CurrentClipboardSettings.Density;
        Dispatcher.UIThread.Post(() => EnsureClipboardViewer().Toggle(density));
    }

    // A new item arrived over the WebSocket: decrypt Text (the UI never sees ciphertext), map to the
    // history-item shape and push it as a "clipboard:item" event so the open lists update live. Sent
    // to BOTH live consumers — the floating viewer and the main window, whose in-app clipboard view
    // inserts the row at the top of its open list (without the main push it stayed frozen until a
    // manual reload). Best-effort: a push failure must not break the receive loop.
    private async void OnClipboardItemReceived(ZyncMaster.Engine.ClipboardEntry entry)
    {
        var engine = _engineHost;
        if (engine == null)
            return;

        try
        {
            byte[]? textKey = await engine.ClipboardKeys.LoadTextKeyAsync(_shutdown.Token);
            PushClipboardItemToUis(engine.Actions.ToHistoryItem(entry, textKey));
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            engine.Logger.Log(LogLevel.Warning, "Clipboard live push failed.", ex);
        }
    }

    // This device just published a capture of its OWN clipboard. The server broadcaster echoes the
    // item to every device EXCEPT the origin, so no ItemReceived will ever follow for it here —
    // without this push a machine never sees its own copies in the dashboard view or the floating
    // viewer. The entry is the captured plaintext (the encrypted copy went to the transport) already
    // stamped with this device's id/name as origin, and the service raises it only after a REAL
    // publish: dedupe-dropped duplicates, echoes and failed publishes never reach this handler.
    private void OnClipboardItemPublished(ZyncMaster.Engine.ClipboardEntry entry)
    {
        var engine = _engineHost;
        if (engine == null)
            return;

        try
        {
            // textKey null is fine: a locally captured Text entry already carries its plaintext.
            PushClipboardItemToUis(engine.Actions.ToHistoryItem(entry, textKey: null));
        }
        catch (Exception ex)
        {
            engine.Logger.Log(LogLevel.Warning, "Clipboard published-item push failed.", ex);
        }
    }

    // Fans one history item out to both live "clipboard:item" consumers: the main window (the in-app
    // clipboard view) and the floating viewer. Each push is independently best-effort, mirroring the
    // other clipboard pushes — one stuck WebView must not silence the other.
    private void PushClipboardItemToUis(ClipboardHistoryItem item)
    {
        try { _bridge?.PushClipboardItem(item); }
        catch (Exception ex) { _engineHost?.Logger.Log(LogLevel.Warning, "Clipboard item push failed.", ex); }
        try { _clipboardViewerBridge?.PushClipboardItem(item); }
        catch (Exception ex) { _engineHost?.Logger.Log(LogLevel.Warning, "Clipboard item viewer push failed.", ex); }
    }

    // The live online roster changed (a server presence frame arrived, or the socket dropped and the
    // cache was cleared): push it to the main-window bridge so an open clipboard devices/settings screen
    // re-fetches and repaints the online dots + "(N online)" count. Best-effort — a push failure must
    // not break the receive loop.
    private void OnClipboardPresenceChanged(System.Collections.Generic.IReadOnlyList<string> onlineDeviceIds)
    {
        try { _bridge?.PushClipboardPresence(onlineDeviceIds); }
        catch (Exception ex) { _engineHost?.Logger.Log(LogLevel.Warning, "Clipboard presence push failed.", ex); }
    }

    // A sibling window changed one device's per-device clipboard settings (send/receive/autoSync), the
    // server broadcast it, and the transport relayed it: push it to the main-window bridge so an open
    // settings screen updates that device's toggles live. Best-effort.
    private void OnClipboardSettingsChanged(string deviceId, ZyncMaster.Engine.ClipboardSettings settings)
    {
        try { _bridge?.PushClipboardSettings(deviceId, settings); }
        catch (Exception ex) { _engineHost?.Logger.Log(LogLevel.Warning, "Clipboard settings push failed.", ex); }
    }

    // The E2E text key was just received/replaced (a peer relayed it after our request): push a
    // "clipboard:key" refresh signal to BOTH the main-window bridge (the dashboard clipboard screen)
    // and the floating viewer bridge so open history lists re-fetch — rows that showed the
    // cannot-decrypt placeholder become readable. Best-effort, mirroring the other clipboard pushes.
    private void OnClipboardTextKeyChanged()
    {
        try { _bridge?.PushClipboardKeyChanged(); }
        catch (Exception ex) { _engineHost?.Logger.Log(LogLevel.Warning, "Clipboard key push failed.", ex); }
        try { _clipboardViewerBridge?.PushClipboardKeyChanged(); }
        catch (Exception ex) { _engineHost?.Logger.Log(LogLevel.Warning, "Clipboard key viewer push failed.", ex); }
    }

    // Another device (or the human panel) deleted a history entry and the server broadcast it: push the
    // deletion to BOTH the main-window bridge (the dashboard clipboard screen) and the floating viewer
    // bridge so each open list drops the row live. Best-effort — a push failure must not break the loop.
    private void OnClipboardDeleted(string id)
    {
        try { _bridge?.PushClipboardDeleted(id); }
        catch (Exception ex) { _engineHost?.Logger.Log(LogLevel.Warning, "Clipboard deleted push failed.", ex); }
        try { _clipboardViewerBridge?.PushClipboardDeleted(id); }
        catch (Exception ex) { _engineHost?.Logger.Log(LogLevel.Warning, "Clipboard deleted viewer push failed.", ex); }
    }

    // Creates (once) the clipboard viewer window + its own WebView2 host + bridge over the shared
    // EngineActions, so the viewer page can call the clipboard bridge actions and receive the live
    // "clipboard:item" push. Reused across hotkey presses (the window hides instead of closing).
    private ClipboardViewerWindow EnsureClipboardViewer()
    {
        if (_clipboardViewer != null)
            return _clipboardViewer;

        var viewer = new ClipboardViewerWindow();

#if WIN_WEBVIEW2
        if (OperatingSystem.IsWindows())
        {
            // App-local paste-panel opacity (0..100, clamped in AppSettingsResolver). Injected into the
            // viewer document as the --cb-paste-opacity CSS variable, and paired with a transparent
            // WebView2/window background so only the (semi-transparent) glass card shows over the desktop.
            var opacity = _engineHost?.Settings.PastePanelOpacity ?? 70;
            var host = new WebView2WebHost(
                startPage: "clipboard-viewer.html",
                documentCreatedScript: WebView2WebHost.BuildPasteOpacityScript(opacity),
                transparentBackground: true);
            _clipboardViewerHost = host;
            if (_engineHost != null)
                _clipboardViewerBridge = new UiBridge(
                    new WebViewBridgeTransport((IBridgeTransport)host),
                    _engineHost.Actions);
            viewer.AttachWebHost(host);
        }
#endif

        _clipboardViewer = viewer;
        return viewer;
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

    private static bool IsVerboseLaunch(string[]? args)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("ZYNCMASTER_VERBOSE"), "1", StringComparison.Ordinal))
            return true;
        if (args == null)
            return false;
        foreach (var a in args)
            if (string.Equals(a, "--verbose", StringComparison.OrdinalIgnoreCase)
                || string.Equals(a, "-v", StringComparison.OrdinalIgnoreCase))
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
        // Use the SAME user-writable settings path as EngineHost so that, when the user fixes the
        // config from the UI, SaveConfigAsync writes to %LOCALAPPDATA% (writable) rather than next
        // to the exe (possibly read-only) — which is exactly the location that triggered the crash.
        var settingsPath = EngineHost.DefaultSettingsPath();
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
        catch (Exception ex)
        {
            // Manual sync failures surface as the next status push; never crash the app.
            _engineHost?.Logger.Log(LogLevel.Warning, "Manual sync (tray) failed.", ex);
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
