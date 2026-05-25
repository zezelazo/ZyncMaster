using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SyncMaster.Core;

namespace SyncMaster.CalExport;

public sealed class AppointmentExportService
{
    private readonly ICalendarService                  _calendarService;
    private readonly Func<ExportMode, IAppointmentExporter> _exporterFactory;
    private readonly IFileSystem                       _fileSystem;
    private readonly IConsoleIO                        _console;

    public AppointmentExportService(
        ICalendarService                       calendarService,
        Func<ExportMode, IAppointmentExporter> exporterFactory,
        IFileSystem                            fileSystem,
        IConsoleIO                             console)
    {
        _calendarService = calendarService ?? throw new ArgumentNullException(nameof(calendarService));
        _exporterFactory = exporterFactory ?? throw new ArgumentNullException(nameof(exporterFactory));
        _fileSystem      = fileSystem      ?? throw new ArgumentNullException(nameof(fileSystem));
        _console         = console         ?? throw new ArgumentNullException(nameof(console));
    }

    /// <summary>
    /// Exports appointments for the given <paramref name="parameters"/> to
    /// <paramref name="outputDirectory"/>. Returns the written file path.
    /// </summary>
    public string Export(ExportParameters parameters, string outputDirectory)
    {
        if (parameters      == null) throw new ArgumentNullException(nameof(parameters));
        if (outputDirectory == null) throw new ArgumentNullException(nameof(outputDirectory));

        var records = _calendarService.GetAppointments(parameters);

        var sorted = records
            .OrderBy(r => r.Start)
            .ToList();

        var exporter = _exporterFactory(parameters.Mode);

        var calendarNames = parameters.SelectedFolders == null
            ? new List<string> { "all" }
            : parameters.SelectedFolders.Select(f => f.DisplayName).ToList();

        var context = new ExportContext
        {
            Year                 = parameters.Year,
            Month                = parameters.Month,
            MonthName            = MonthNames.Get(parameters.Month),
            CalendarDisplayNames = calendarNames,
            ExportedAt           = DateTimeOffset.Now,
        };

        var content  = exporter.Serialize(sorted, context);
        var dateTag  = DateTime.Now.ToString("yyyyMMdd");
        var fileName = $"Calendar_{parameters.Year}_{context.MonthName}_{exporter.FileSuffix}_{dateTag}.{exporter.FileExtension}";
        var filePath = Path.Combine(outputDirectory, fileName);

        _fileSystem.WriteAllText(filePath, content, Encoding.UTF8);

        _console.WriteLine($"Done. {sorted.Count} event(s) exported.");
        _console.WriteLine($"File: {filePath}");

        return filePath;
    }
}
