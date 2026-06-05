using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using ZyncMaster.App.Bridge;
using ZyncMaster.Core;
using ZyncMaster.Engine;

namespace ZyncMaster.App.Configuration;

// Builds the live EngineActions from settings on disk, mirroring the Cli's Program.cs
// composition. Kept out of App.axaml.cs so the wiring is in one place and the Avalonia
// lifecycle code stays thin. Returns null state details via the loaded settings so the
// caller can start a SyncLoop on the resolved interval.
public sealed class EngineHost : IDisposable
{
    public EngineActions Actions { get; }
    public EngineSettings Settings { get; }

    // The raw sync cycle (SyncEngine) the background loop drives, wrapped by the app's
    // StatusPushingCycle. Exposed separately from Actions because the loop needs the
    // ISyncCycle, while the bridge needs the IEngineActions facade. The PairScheduler is
    // built separately by the App from the pieces it needs.
    public ISyncCycle SyncCycle { get; }

    // The multi-pair scheduler the App runs in the background instead of the single SyncLoop.
    public PairScheduler Scheduler { get; }

    // FIX C — keeps the device's server-side lease alive while the App runs so the cron fallback
    // skips this user's pairs (no double sync). Driven by the App alongside the scheduler.
    public DeviceHeartbeatLoop HeartbeatLoop { get; }

    private readonly HttpClient _http;

    private EngineHost(
        EngineActions actions, ISyncCycle syncCycle, PairScheduler scheduler,
        DeviceHeartbeatLoop heartbeatLoop, EngineSettings settings, HttpClient http)
    {
        Actions = actions;
        SyncCycle = syncCycle;
        Scheduler = scheduler;
        HeartbeatLoop = heartbeatLoop;
        Settings = settings;
        _http = http;
    }

    // Builds the engine from settings.json next to the exe (created with defaults if
    // absent). Throws SettingsValidationException if the resolved settings are invalid
    // and SettingsLoadException if the file exists but cannot be parsed.
    //
    // saveDialog: shows a Save-As picker (the App supplies an Avalonia IStorageProvider
    // implementation) and returns the chosen path or null when cancelled.
    // autoStartExePath: the host executable registered for login auto-start.
    public static EngineHost Create(Func<string, Task<string?>> saveDialog, string autoStartExePath)
    {
        var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                     ?? Directory.GetCurrentDirectory();
        var settingsPath = Path.Combine(exeDir, "settings.json");

        var fileSystem = new PhysicalFileSystem();
        var settingsRepo = new SettingsRepository<AppSettings>(fileSystem);
        var resolver = new AppSettingsResolver();

        var appSettings = settingsRepo.LoadOrCreateDefault(settingsPath);
        var engineSettings = resolver.Resolve(appSettings);

        var clock = new SystemClock();

        var keyStorePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZyncMaster", "App", "device.key");
        var keyStore = Platform.KeyStoreFactory.Create(
            new Platform.DefaultPlatform(), keyStorePath, clock);

        var http = new HttpClient();
        var pairingClient = new HttpPairingClient(http, engineSettings.ServerBaseUrl);
        var syncClient = new HttpSyncClient(http, engineSettings.ServerBaseUrl);
        var pairsClient = new HttpPairsClient(http, engineSettings.ServerBaseUrl);

        var calExportRunner = new CalExportRunner(engineSettings.CalExportPath);
        var calendarReader = new CompleteCalendarReader();
        var calendarSource = new OutlookComSource(calExportRunner, calendarReader, engineSettings.CalendarNames);

        var browser = new DefaultBrowserLauncher();
        var pairingService = new PairingService(pairingClient, browser, keyStore, engineSettings);
        var syncEngine = new SyncEngine(keyStore, calendarSource, syncClient, clock, engineSettings);

        var txtExporter = new BasicTxtExporter(calExportRunner);
        var autoStart = new WindowsAutoStartManager(new WindowsRegistry());

        // Identity (sign-in) wiring (Task 2e). The DPAPI-encrypted token cache lives next to the
        // device key under %LOCALAPPDATA%\ZyncMaster\App\; the login service brokers sign-in over
        // a system browser + an ephemeral HttpListener loopback (mirroring PairingService) and
        // talks to the Server's identity endpoints through HttpIdentityServerClient.
        var identityCache = Platform.FileIdentityTokenCache.CreateDefault();
        var identityServer = new Platform.HttpIdentityServerClient(http, engineSettings.ServerBaseUrl);
        var identityLogin = new IdentityLoginService(
            identityServer,
            identityCache,
            () => new Platform.HttpListenerIdentityLoopback(),
            new Platform.DefaultSystemBrowser(),
            engineSettings.ServerBaseUrl);

        // Calendar-account connect wiring: reuses the SAME identity token cache (for the
        // IdentityBearer) and the SAME loopback technique as sign-in, but binds the listener to
        // /calendar/callback/ and talks to the Server's /api/calendar endpoints over the bearer.
        var calendarServer = new Platform.HttpCalendarServerClient(http, engineSettings.ServerBaseUrl);
        var calendarConnect = new CalendarConnectService(
            calendarServer,
            identityCache,
            () => new Platform.HttpListenerIdentityLoopback("/calendar/callback/"),
            new Platform.DefaultSystemBrowser());

        // COM availability probe (HKCR Outlook.Application ProgID). Untested OS wrapper like
        // WindowsRegistry; gates the COM-only source tile + local .txt export in the UI.
        var comProbe = new WindowsOutlookComProbe();

        var actions = new EngineActions(
            keyStore, pairingService, syncEngine, settingsRepo, resolver, settingsPath,
            pairsClient, identityCache, txtExporter, autoStart, engineSettings, saveDialog, autoStartExePath,
            identityLogin,
            calendarConnect,
            comProbe,
            calendarSource,
            clock,
            http,
            ownedHttp: null);

        // Multi-pair scheduler: drives every configured pair on its own cadence. COM-sourced
        // pairs are read locally and pushed; the rest are mirrored server-side. It lists the pairs
        // with the signed-in user's identity bearer (human-only surface) and pushes/runs under the
        // device api key, so it is given an IIdentityTokenProvider over the same identity cache.
        var identityTokenProvider = new IdentityTokenCacheProvider(identityCache);
        var scheduler = new PairScheduler(
            pairsClient, calendarSource, keyStore, identityTokenProvider, clock, engineSettings);

        // FIX C — the device-lease heartbeat. Uses the SAME pairs client + device key store as the
        // scheduler; a tick with no device key (unpaired) is a clean no-op.
        var heartbeatLoop = new DeviceHeartbeatLoop(pairsClient, keyStore);

        return new EngineHost(actions, syncEngine, scheduler, heartbeatLoop, engineSettings, http);
    }

    public void Dispose()
    {
        Actions.Dispose();
        _http.Dispose();
    }
}
