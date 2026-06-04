using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ZyncMaster.Engine;

public interface ICalExportRunner
{
    Task<string> ExportMonthAsync(int year, int month, IReadOnlyList<string>? calendarNames, CancellationToken ct);

    // Exports a single month in Simple (pipe-delimited .txt) mode to the given output file path.
    // includeCancelled controls whether cancelled events are kept in the output (CalExport's
    // includeCancelled config flag); calendarNames filters which local Outlook calendars by
    // display name (null/empty → all).
    Task ExportSimpleAsync(int year, int month, IReadOnlyList<string>? calendarNames, bool includeCancelled, string outputFilePath, CancellationToken ct);
}
