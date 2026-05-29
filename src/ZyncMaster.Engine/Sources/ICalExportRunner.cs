using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ZyncMaster.Engine;

public interface ICalExportRunner
{
    Task<string> ExportMonthAsync(int year, int month, IReadOnlyList<string>? calendarNames, CancellationToken ct);

    // Exports a single month in Simple (pipe-delimited .txt) mode to the given output file path.
    Task ExportSimpleAsync(int year, int month, IReadOnlyList<string>? calendarNames, string outputFilePath, CancellationToken ct);
}
