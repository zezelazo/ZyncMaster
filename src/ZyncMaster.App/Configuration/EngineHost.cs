using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
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
    // ISyncCycle, while the bridge needs the IEngineActions facade.
    public ISyncCycle SyncCycle { get; }

    private readonly HttpClient _http;

    private EngineHost(EngineActions actions, ISyncCycle syncCycle, EngineSettings settings, HttpClient http)
    {
        Actions = actions;
        SyncCycle = syncCycle;
        Settings = settings;
        _http = http;
    }

    // Builds the engine from settings.json next to the exe (created with defaults if
    // absent). Throws SettingsValidationException if the resolved settings are invalid
    // and SettingsLoadException if the file exists but cannot be parsed.
    public static EngineHost Create()
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

        var calExportRunner = new CalExportRunner(engineSettings.CalExportPath);
        var calendarReader = new CompleteCalendarReader();
        var calendarSource = new OutlookComSource(calExportRunner, calendarReader, engineSettings.CalendarNames);

        var browser = new DefaultBrowserLauncher();
        var pairingService = new PairingService(pairingClient, browser, keyStore, engineSettings);
        var syncEngine = new SyncEngine(keyStore, calendarSource, syncClient, clock, engineSettings);

        var actions = new EngineActions(
            keyStore, pairingService, syncEngine, settingsRepo, resolver, settingsPath, ownedHttp: null);

        return new EngineHost(actions, syncEngine, engineSettings, http);
    }

    public void Dispose()
    {
        Actions.Dispose();
        _http.Dispose();
    }
}
