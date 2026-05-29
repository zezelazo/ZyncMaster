using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using ZyncMaster.CalImport;
using ZyncMaster.Core;
using ZyncMaster.Graph;

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("=== Outlook Calendar Import ===");
Console.WriteLine();

var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Directory.GetCurrentDirectory();

ParsedImportArguments parsedArgs;
try
{
    parsedArgs = new ArgumentParser().Parse(args);
}
catch (ArgumentParsingException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Usage: CalImport [-s|--source <path>] [-a|--auto] [-c|--config <path>]");
    Console.Error.WriteLine("                 [-k|--calendar <id>] [-n|--new-calendar <name>] [--dry-run]");
    // Direct Environment.Exit is intentional here: the composition root has not run yet,
    // so IApplicationTerminator does not exist. This is the only allowed direct exit path
    // outside the runner pipeline.
    Environment.Exit(1);
    return;
}

var fileSystem        = new PhysicalFileSystem();
var console           = new ConsoleIO();
var terminator        = new ConsoleApplicationTerminator(console);
var appConfigRepo     = new SettingsRepository<AppConfig>(fileSystem);
var settingsRepo      = new SettingsRepository<ImportSettings>(fileSystem);
var settingsResolver  = new ImportSettingsResolver();
var importSource      = new JsonImportSource(fileSystem);
var planBuilder       = new ImportPlanBuilder();
var participantRenderer = new ParticipantBodyRenderer();
var draftBuilder      = new EventDraftBuilder(participantRenderer);
var calendarPicker    = new CalendarPicker(console, terminator);

// Deployment config (clientId / authority / extendedPropertyGuid). Loaded here in the
// composition root, before the runner, because a missing clientId gates everything else.
var appConfigPath = Path.Combine(exeDir, "appsettings.json");
AppConfig appConfig;
try
{
    appConfig = appConfigRepo.LoadOrCreateDefault(appConfigPath);
}
catch (SettingsLoadException ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    Environment.Exit(1);
    return;
}

if (string.IsNullOrWhiteSpace(appConfig.ClientId))
{
    Console.Error.WriteLine($"Error: 'clientId' is empty in {appConfigPath}.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Register a public-client app in portal.azure.com, then put its id in appsettings.json:");
    Console.Error.WriteLine("  1. Azure Active Directory > App registrations > New registration");
    Console.Error.WriteLine("  2. Supported account types: 'Personal Microsoft accounts only'");
    Console.Error.WriteLine("  3. Redirect URI: 'Public client/native (mobile & desktop)' -> http://localhost");
    Console.Error.WriteLine("  4. API permissions: Microsoft Graph > Delegated > Calendars.ReadWrite, User.Read");
    Console.Error.WriteLine("  5. Authentication > Allow public client flows = Yes");
    Console.Error.WriteLine("  6. Copy the Application (client) ID into 'clientId' in appsettings.json");
    Environment.Exit(1);
    return;
}

// One HttpClient for the process — reused across requests.
var sharedHttp = new HttpClient();

ICalendarTarget BuildCalendarTarget(ImportSettings settings)
{
    var authority   = settingsResolver.ResolveAuthority(appConfig);
    var extPropGuid = settingsResolver.ResolveExtendedPropertyGuid(appConfig);
    var cacheDir    = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                   "ZyncMaster", "CalImport");
    var authenticator = new GraphAuthenticator(appConfig.ClientId, authority, settings.AccountHint, cacheDir);
    return new GraphCalendarTarget(sharedHttp, authenticator, extPropGuid);
}

var runner = new ApplicationRunner(
    console, terminator, fileSystem, settingsRepo, settingsResolver,
    importSource, planBuilder, draftBuilder, calendarPicker,
    BuildCalendarTarget, exeDir);

try
{
    runner.Run(parsedArgs);
}
catch (SettingsLoadException ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    sharedHttp.Dispose();
    Environment.Exit(1);
}
catch (SettingsValidationException ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    sharedHttp.Dispose();
    Environment.Exit(1);
}
catch (AuthenticationFailedException ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    sharedHttp.Dispose();
    Environment.Exit(1);
}
catch (ImportSourceException ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    sharedHttp.Dispose();
    Environment.Exit(1);
}
catch (InvalidOperationException ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    sharedHttp.Dispose();
    Environment.Exit(1);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Operation cancelled by user.");
    sharedHttp.Dispose();
    Environment.Exit(130);
}
finally
{
    sharedHttp.Dispose();
}
