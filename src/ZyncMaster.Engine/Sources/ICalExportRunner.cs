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

    // Enumerates the local Outlook calendar folders by driving CalExport in --list-calendars mode
    // and returns their display names (the same "{Name} [{store}]" labels the interactive prompts
    // and the "calendars" config use). Read-only: it never opens or modifies a calendar. Used to
    // populate the wizard's COM source multi-select.
    Task<IReadOnlyList<string>> ListCalendarsAsync(CancellationToken ct);
}
