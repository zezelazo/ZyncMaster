using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using SyncMaster.CalImport;
using SyncMaster.Core;

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
var settingsRepo      = new SettingsRepository<ImportSettings>(fileSystem);
var settingsResolver  = new ImportSettingsResolver();
var importSource      = new JsonImportSource(fileSystem);
var planBuilder       = new ImportPlanBuilder();
var participantRenderer = new ParticipantBodyRenderer();
var draftBuilder      = new EventDraftBuilder(participantRenderer);
var calendarPicker    = new CalendarPicker(console, terminator);

// One HttpClient for the process — reused across requests.
var sharedHttp = new HttpClient();

ICalendarTarget BuildCalendarTarget(ImportSettings settings)
{
    var authority   = settingsResolver.ResolveAuthority(settings);
    var extPropGuid = settingsResolver.ResolveExtendedPropertyGuid(settings);
    var cacheDir    = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                   "SyncMaster", "CalImport");
    var authenticator = new GraphAuthenticator(settings.ClientId, authority, settings.AccountHint, cacheDir);
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
