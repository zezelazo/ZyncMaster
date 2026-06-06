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

    // The device-side local logger, exposed so App can log into the same per-day file from its
    // own catch blocks (scheduler loop, SafeSyncNow, the status loop).
    public IAppLogger Logger { get; }

    // The multi-pair scheduler the App runs in the background instead of the single SyncLoop.
    public PairScheduler Scheduler { get; }

    // FIX C — keeps the device's server-side lease alive while the App runs so the cron fallback
    // skips this user's pairs (no double sync). Driven by the App alongside the scheduler.
    public DeviceHeartbeatLoop HeartbeatLoop { get; }

    private readonly HttpClient _http;

    private EngineHost(
        EngineActions actions, PairScheduler scheduler,
        DeviceHeartbeatLoop heartbeatLoop, EngineSettings settings, HttpClient http, IAppLogger logger)
    {
        Actions = actions;
        Scheduler = scheduler;
        HeartbeatLoop = heartbeatLoop;
        Settings = settings;
        _http = http;
        Logger = logger;
    }

    // The user-writable settings path: %LOCALAPPDATA%\ZyncMaster\App\settings.json. This is the
    // SAME user-writable tree as device.key / identity.token / the WebView2 user data, so a first
    // launch from a read-only install location (Program Files, a still-mounted zip) can always write
    // settings — unlike the old location next to the exe, which threw UnauthorizedAccessException /
    // IOException and crashed the app on a fresh machine.
    public static string DefaultSettingsPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZyncMaster", "App", "settings.json");

    // The legacy location next to the exe. Kept only so an existing install's settings can be
    // migrated to the new user-writable path once, on first run after the move.
    private static string LegacySettingsPath()
    {
        var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                     ?? Directory.GetCurrentDirectory();
        return Path.Combine(exeDir, "settings.json");
    }

    // One-time migration: if the old settings.json exists next to the exe and the new
    // user-writable copy does not yet exist, copy the old file across so an upgrading user keeps
    // their configuration. Best-effort — a failed copy (read-only source, locked file) must not
    // crash; the app falls back to generating defaults at the new path. The legacy file is left in
    // place (the install dir may be read-only, and leaving it is harmless).
    private static void MigrateLegacySettingsIfNeeded(string newPath)
    {
        try
        {
            var legacy = LegacySettingsPath();
            if (!File.Exists(legacy) || File.Exists(newPath))
                return;

            var dir = Path.GetDirectoryName(newPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.Copy(legacy, newPath, overwrite: false);
        }
        catch (IOException) { /* best-effort migration */ }
        catch (UnauthorizedAccessException) { /* best-effort migration */ }
    }

    // Builds the engine from settings.json under %LOCALAPPDATA%\ZyncMaster\App\ (created with
    // defaults if absent, migrated once from the legacy location next to the exe). Throws
    // SettingsValidationException if the resolved settings are invalid and SettingsLoadException if
    // the file exists but cannot be parsed.
    //
    // saveDialog: shows a Save-As picker (the App supplies an Avalonia IStorageProvider
    // implementation) and returns the chosen path or null when cancelled.
    // autoStartExePath: the host executable registered for login auto-start.
    public static EngineHost Create(Func<string, Task<string?>> saveDialog, string autoStartExePath, bool verbose = false)
    {
        var settingsPath = DefaultSettingsPath();
        MigrateLegacySettingsIfNeeded(settingsPath);

        var fileSystem = new PhysicalFileSystem();
        var settingsRepo = new SettingsRepository<AppSettings>(fileSystem);
        var resolver = new AppSettingsResolver();

        var appSettings = settingsRepo.LoadOrCreateDefault(settingsPath);
        var engineSettings = resolver.Resolve(appSettings);

        var clock = new SystemClock();

        // Device-side local logger. Default level Warning/Error; verbose lowers it to Debug. Also
        // honour ZYNCMASTER_VERBOSE=1 so the tray app can be made verbose without args. Log the
        // resolved path up front (independent of level) so the user knows where to look.
        var verboseEffective = verbose
            || string.Equals(Environment.GetEnvironmentVariable("ZYNCMASTER_VERBOSE"), "1", StringComparison.Ordinal);
        var logger = new DailyFileLogger(
            DailyFileLogger.DefaultLogDirectory(),
            verboseEffective ? LogLevel.Debug : LogLevel.Warning,
            () => clock.UtcNow);
        logger.Log(LogLevel.Info, $"Logging to {logger.CurrentLogFilePath()} (verbose={verboseEffective}).");

        var keyStorePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZyncMaster", "App", "device.key");
        var keyStore = Platform.KeyStoreFactory.Create(
            new Platform.DefaultPlatform(), keyStorePath, clock);

        var http = new HttpClient();
        var pairingClient = new HttpPairingClient(http, engineSettings.ServerBaseUrl);
        var syncClient = new HttpSyncClient(http, engineSettings.ServerBaseUrl);
        var pairsClient = new HttpPairsClient(http, engineSettings.ServerBaseUrl);

        var calExportRunner = new CalExportRunner(engineSettings.CalExportPath, logger, engineSettings.CalExportTimeoutMinutes);
        var calendarReader = new CompleteCalendarReader();
        var calendarSource = new OutlookComSource(calExportRunner, calendarReader, engineSettings.CalendarNames, logger);

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
            calExportRunner,
            clock,
            http,
            logger,
            ownedHttp: null);

        // Multi-pair scheduler: drives every configured pair on its own cadence. COM-sourced
        // pairs are read locally and pushed; the rest are mirrored server-side. It lists the pairs
        // with the signed-in user's identity bearer (human-only surface) and pushes/runs under the
        // device api key, so it is given an IIdentityTokenProvider over the same identity cache.
        var identityTokenProvider = new IdentityTokenCacheProvider(identityCache);
        var scheduler = new PairScheduler(
            pairsClient, calendarSource, keyStore, identityTokenProvider, clock, engineSettings, logger);

        // FIX C — the device-lease heartbeat. Uses the SAME pairs client + device key store as the
        // scheduler; a tick with no device key (unpaired) is a clean no-op.
        var heartbeatLoop = new DeviceHeartbeatLoop(pairsClient, keyStore, logger: logger);

        return new EngineHost(actions, scheduler, heartbeatLoop, engineSettings, http, logger);
    }

    public void Dispose()
    {
        Actions.Dispose();
        _http.Dispose();
    }
}
