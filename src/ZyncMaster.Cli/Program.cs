using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ZyncMaster.Cli;
using ZyncMaster.Core;
using ZyncMaster.Engine;

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("=== ZyncMaster Sync Host ===");
Console.WriteLine();

var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Directory.GetCurrentDirectory();

// --- Argument parsing -------------------------------------------------------
// Modes are mutually exclusive in intent but resolved in priority order:
//   --pair          run pairing then exit
//   --once          single sync cycle then exit
//   (default)       pair-if-needed then loop forever
// Overrides (independent of mode):
//   --interval <m>  override interval minutes
//   --server <url>  override server base url
var mode = "loop";
int? intervalOverride = null;
string? serverOverride = null;

for (var i = 0; i < args.Length; i++)
{
    var arg = args[i];
    switch (arg)
    {
        case "--pair":
            mode = "pair";
            break;
        case "--once":
            mode = "once";
            break;
        case "--interval":
            if (i + 1 >= args.Length || !int.TryParse(args[++i], out var minutes))
            {
                Console.Error.WriteLine("Error: --interval requires a number of minutes.");
                Environment.Exit(1);
                return;
            }
            intervalOverride = minutes;
            break;
        case "--server":
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine("Error: --server requires a URL.");
                Environment.Exit(1);
                return;
            }
            serverOverride = args[++i];
            break;
        default:
            Console.Error.WriteLine($"Error: unknown argument '{arg}'.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Usage: ZyncMaster.Cli [--pair | --once] [--interval <minutes>] [--server <url>]");
            Environment.Exit(1);
            return;
    }
}

// --- Settings ---------------------------------------------------------------
var fileSystem = new PhysicalFileSystem();
var settingsRepo = new SettingsRepository<CliSettings>(fileSystem);
var resolver = new CliSettingsResolver();

// settings.json (user prefs) takes precedence; fall back to appsettings.json (deployment copy).
var settingsPath = Path.Combine(exeDir, "settings.json");
var appSettingsPath = Path.Combine(exeDir, "appsettings.json");

CliSettings cliSettings;
try
{
    cliSettings = settingsRepo.Exists(settingsPath)
        ? settingsRepo.LoadOrCreateDefault(settingsPath)
        : settingsRepo.LoadOrCreateDefault(appSettingsPath);
}
catch (SettingsLoadException ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    Environment.Exit(1);
    return;
}

// CLI overrides win over file settings.
if (serverOverride != null) cliSettings.ServerBaseUrl = serverOverride;
if (intervalOverride.HasValue) cliSettings.IntervalMinutes = intervalOverride.Value;

EngineSettings engineSettings;
try
{
    engineSettings = resolver.Resolve(cliSettings);
}
catch (SettingsValidationException ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    Environment.Exit(1);
    return;
}

// --- Composition root -------------------------------------------------------
var clock = new SystemClock();

var keyStorePath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "ZyncMaster", "Cli", "device.key");
var keyStore = new DpapiDeviceKeyStore(keyStorePath, clock);

using var http = new HttpClient();
var pairingClient = new HttpPairingClient(http, engineSettings.ServerBaseUrl);
var syncClient = new HttpSyncClient(http, engineSettings.ServerBaseUrl);

var calExportRunner = new CalExportRunner(engineSettings.CalExportPath);
var calendarReader = new CompleteCalendarReader();
var calendarSource = new OutlookComSource(calExportRunner, calendarReader, engineSettings.CalendarNames);

var browser = new DefaultBrowserLauncher();
var pairingService = new PairingService(pairingClient, browser, keyStore, engineSettings);
var syncEngine = new SyncEngine(keyStore, calendarSource, syncClient, clock, engineSettings);

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // Don't terminate immediately — let the loop unwind cleanly.
    cts.Cancel();
};

try
{
    switch (mode)
    {
        case "pair":
        {
            var outcome = await pairingService.EnsurePairedAsync(cts.Token);
            PrintPairingOutcome(outcome);
            Environment.Exit(outcome.Success ? 0 : 1);
            break;
        }

        case "once":
        {
            var paired = await pairingService.EnsurePairedAsync(cts.Token);
            PrintPairingOutcome(paired);
            if (!paired.Success)
            {
                Environment.Exit(1);
                break;
            }

            var result = await syncEngine.RunCycleAsync(cts.Token);
            PrintSyncResult(result);
            break;
        }

        default: // loop
        {
            var paired = await pairingService.EnsurePairedAsync(cts.Token);
            PrintPairingOutcome(paired);
            if (!paired.Success)
            {
                Environment.Exit(1);
                break;
            }

            var interval = TimeSpan.FromMinutes(engineSettings.IntervalMinutes);
            Console.WriteLine($"Syncing every {engineSettings.IntervalMinutes} minute(s). Press Ctrl+C to stop.");
            var loop = new SyncLoop(syncEngine, interval);
            await loop.RunAsync(cts.Token);
            Console.WriteLine("Stopped.");
            break;
        }
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("Cancelled.");
}

static void PrintPairingOutcome(PairingOutcome outcome)
{
    if (outcome.Code != null)
        Console.WriteLine($"Pairing code: {outcome.Code}");

    if (outcome.Success)
        Console.WriteLine("Device is paired.");
    else
        Console.Error.WriteLine("Pairing failed: " + (outcome.Message ?? "unknown error."));
}

static void PrintSyncResult(SyncResult result)
{
    if (result.Skipped)
    {
        Console.WriteLine("Sync skipped: " + (result.SkipReason ?? "unknown reason."));
        return;
    }

    var push = result.Push;
    if (push == null)
    {
        Console.WriteLine("Sync produced no result.");
        return;
    }

    if (push.NoConnectedAccount)
    {
        Console.WriteLine("Sync skipped: no Microsoft account connected on the server yet.");
        return;
    }

    Console.WriteLine(
        $"Sync complete: created {push.Created}, updated {push.Updated}, " +
        $"deleted {push.Deleted}, skipped {push.Skipped}.");
    if (push.Failures.Count > 0)
        Console.WriteLine($"Failures ({push.Failures.Count}): {string.Join("; ", push.Failures)}");
}
