using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ZyncMaster.Engine;

// Produces a Simple-mode (pipe-delimited .txt) export of a single month to a file on
// disk, for users who want a plain human-readable dump rather than the JSON used by sync.
// Delegates the actual export to ICalExportRunner so the process boundary stays untested.
public sealed class BasicTxtExporter
{
    private readonly ICalExportRunner _runner;

    public BasicTxtExporter(ICalExportRunner runner)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public Task ExportAsync(int year, int month, IReadOnlyList<string>? calendarNames, bool includeCancelled, string outputFilePath, CancellationToken ct)
    {
        if (outputFilePath == null) throw new ArgumentNullException(nameof(outputFilePath));
        return _runner.ExportSimpleAsync(year, month, calendarNames, includeCancelled, outputFilePath, ct);
    }
}
