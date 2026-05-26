using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SyncMaster.Engine;

public interface ICalExportRunner
{
    Task<string> ExportMonthAsync(int year, int month, IReadOnlyList<string>? calendarNames, CancellationToken ct);
}
