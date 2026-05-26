using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SyncMaster.Core;

namespace SyncMaster.Engine;

// Reads a date window from the local Outlook calendar by driving CalExport over the
// set of months the window touches, parsing each month's Complete-mode JSON, then
// deduping + filtering down to the exact window.
public sealed class OutlookComSource : ICalendarSource
{
    private readonly ICalExportRunner _runner;
    private readonly CompleteCalendarReader _reader;
    private readonly IReadOnlyList<string>? _calendarNames;

    public OutlookComSource(ICalExportRunner runner, CompleteCalendarReader reader, IReadOnlyList<string>? calendarNames)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _calendarNames = calendarNames;
    }

    public async Task<IReadOnlyList<AppointmentRecord>> ReadWindowAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var months = MonthsCovering(fromUtc, toUtc);

        // Dedupe by Id, keeping the first occurrence seen (months iterate in order).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var collected = new List<AppointmentRecord>();

        foreach (var (year, month) in months)
        {
            ct.ThrowIfCancellationRequested();
            var json = await _runner.ExportMonthAsync(year, month, _calendarNames, ct);
            var read = _reader.Parse(json);
            foreach (var ev in read.Events)
            {
                if (seen.Add(ev.Id))
                    collected.Add(ev);
            }
        }

        return collected
            .Where(e => e.StartOffset >= fromUtc && e.StartOffset <= toUtc)
            .OrderBy(e => e.StartOffset)
            .ToList();
    }

    // The local calendar works in wall-clock time, so the set of months that can
    // hold events in the window spans [fromUtc.LocalDateTime.Date, toUtc.LocalDateTime.Date].
    private static IEnumerable<(int Year, int Month)> MonthsCovering(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var start = fromUtc.LocalDateTime.Date;
        var end = toUtc.LocalDateTime.Date;
        if (end < start)
            (start, end) = (end, start);

        var cursor = new DateTime(start.Year, start.Month, 1);
        var last = new DateTime(end.Year, end.Month, 1);

        while (cursor <= last)
        {
            yield return (cursor.Year, cursor.Month);
            cursor = cursor.AddMonths(1);
        }
    }
}
