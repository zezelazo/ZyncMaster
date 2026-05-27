using System;
using System.IO;
using System.Reflection;
using System.Text;
using SyncMaster.CalExport;
using SyncMaster.Core;

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
    Console.Error.WriteLine("Usage: CalExport [-a|--auto] [-c|--config <path>] [-o|--output <path>]");
    // Direct Environment.Exit here is intentional: the terminator is not yet constructed
    // (composition root has not run), so IApplicationTerminator cannot be used.
    // The return below is required only for compiler definite-assignment analysis.
    Environment.Exit(1);
    return;
}

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

runner.Run(parsedArgs);
