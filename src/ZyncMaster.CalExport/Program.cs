using System;
using System.IO;
using System.Reflection;
using System.Text;
using ZyncMaster.CalExport;
using ZyncMaster.Core;

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("=== Outlook Calendar Export ===");
Console.WriteLine();

var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Directory.GetCurrentDirectory();

ParsedArguments parsedArgs;
try
{
    parsedArgs = new ArgumentParser().Parse(args);
}
catch (ArgumentParsingException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Usage: CalExport [-a|--auto] [-c|--config <path>] [-o|--output <path>] [-v|--verbose] [-l|--list-calendars]");
    // Direct Environment.Exit here is intentional: the terminator is not yet constructed
    // (composition root has not run), so IApplicationTerminator cannot be used.
    // The return below is required only for compiler definite-assignment analysis.
    Environment.Exit(1);
    return;
}

// Local daily logger. CalExport is an ephemeral child process driven by the Engine; it logs to
// the SAME per-day file as the App/Engine/Cli so a single day's log holds the whole sync trail.
// Default level Warning/Error; -v/--verbose (propagated by the Engine when it runs verbose) lowers
// it to Debug. ZYNCMASTER_VERBOSE=1 is also honoured for parity with the other tools.
var verbose = parsedArgs.Verbose
    || string.Equals(Environment.GetEnvironmentVariable("ZYNCMASTER_VERBOSE"), "1", StringComparison.Ordinal);
var logger = new DailyFileLogger(
    DailyFileLogger.DefaultLogDirectory(),
    verbose ? LogLevel.Debug : LogLevel.Warning,
    () => DateTimeOffset.UtcNow);
logger.Log(LogLevel.Info, $"Logging to {logger.CurrentLogFilePath()}");
logger.Log(LogLevel.Debug,
    $"CalExport started. auto={parsedArgs.AutoMode} config={parsedArgs.ConfigPath ?? "(none)"} output={parsedArgs.OutputPath ?? "(none)"} verbose={verbose}");

var fileSystem   = new PhysicalFileSystem();
var console      = new ConsoleIO();
var terminator   = new ConsoleApplicationTerminator(console);
var settingsRepo = new SettingsRepository<AppSettings>(fileSystem);
var resolver     = new SettingsResolver();
var matcher      = new CalendarFolderMatcher();
var outputDirSvc = new OutputDirectoryService(fileSystem, console, terminator);
var calService   = new OutlookCalendarService();

IAppointmentExporter SelectExporter(ExportMode mode) => mode switch
{
    ExportMode.Simple   => new SimpleAppointmentExporter(),
    ExportMode.Complete => new CompleteAppointmentExporter(),
    _ => throw new ArgumentOutOfRangeException(nameof(mode))
};

var exportService = new AppointmentExportService(calService, SelectExporter, fileSystem, console);

var runner = new ApplicationRunner(
    console, calService, settingsRepo, fileSystem,
    resolver, matcher, outputDirSvc, exportService, terminator, exeDir);

try
{
    runner.Run(parsedArgs);
    logger.Log(LogLevel.Debug, "CalExport completed.");
}
catch (Exception ex)
{
    // Surface unhandled failures (COM errors, malformed config, IO) into the per-day log with the
    // full exception so a non-zero exit can be diagnosed after the fact. The exception still
    // propagates so the process exit code and stderr (read by the Engine) are unchanged.
    logger.Log(LogLevel.Error, "CalExport failed.", ex);
    throw;
}
