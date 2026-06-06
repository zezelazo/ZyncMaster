using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ZyncMaster.Core;

namespace ZyncMaster.Engine;

// Reads a date window from the local Outlook calendar by driving CalExport over the
// set of months the window touches, parsing each month's Complete-mode JSON, then
// deduping + filtering down to the exact window.
public sealed class OutlookComSource : ICalendarSource
{
    private readonly ICalExportRunner _runner;
    private readonly CompleteCalendarReader _reader;
    private readonly IReadOnlyList<string>? _defaultCalendarNames;
    private readonly IAppLogger _logger;

    public OutlookComSource(ICalExportRunner runner, CompleteCalendarReader reader, IReadOnlyList<string>? calendarNames, IAppLogger? logger = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        // Device-wide default selection (EngineSettings.CalendarNames). Used only as the fallback
        // for a legacy pair whose source carries no per-pair selection; the per-pair selection is
        // resolved by the caller (PairRunner) and passed to ReadWindowAsync.
        _defaultCalendarNames = calendarNames;
        _logger = logger ?? NullAppLogger.Instance;
    }

    public async Task<IReadOnlyList<AppointmentRecord>> ReadWindowAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, IReadOnlyList<string>? calendarNames, CancellationToken ct)
    {
        // The per-pair selection (calendarNames) is authoritative; when the caller passes null it
        // falls back to the device default. A null effective selection means "all calendars".
        var effective = calendarNames ?? _defaultCalendarNames;

        var months = MonthsCovering(fromUtc, toUtc).ToList();

        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.Log(LogLevel.Debug,
                $"OutlookCom read window [{fromUtc:o} .. {toUtc:o}] spans {months.Count} month(s).");

        // Dedupe by Id, keeping the first occurrence seen (months iterate in order).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var collected = new List<AppointmentRecord>();

        foreach (var (year, month) in months)
        {
            ct.ThrowIfCancellationRequested();
            var json = await _runner.ExportMonthAsync(year, month, effective, ct);
            var read = _reader.Parse(json);
            _logger.Log(LogLevel.Debug, $"OutlookCom month {year}-{month:D2}: read {read.Events.Count} event(s).");
            foreach (var ev in read.Events)
            {
                if (seen.Add(ev.Id))
                    collected.Add(ev);
            }
        }

        // DATA-INTEGRITY CONTRACT (read membership == destination sweep membership).
        // The destination sweep enumerates with Graph calendarView (GraphCalendarTarget
        // .ListManagedInWindowAsync), which returns every event that OVERLAPS [from, to] —
        // including an all-day / multi-day event that STARTED before `from` but still runs into
        // the window. If the read filtered by START only (StartOffset >= fromUtc), such an event
        // would be swept-eligible at the destination yet ABSENT from the read set, so the sweep
        // would DELETE a still-live event on every cycle (data loss). To keep read and sweep
        // derived from the SAME criterion, membership is OVERLAP, exactly matching calendarView:
        //   event overlaps the window  <=>  EndOffset > fromUtc AND StartOffset <= toUtc.
        // EndOffset is always populated here (CompleteCalendarReader parses `end` as a required
        // field), so the lower bound is meaningful for every record.
        var filtered = collected
            .Where(e => e.EndOffset > fromUtc && e.StartOffset <= toUtc)
            .OrderBy(e => e.StartOffset)
            .ToList();

        _logger.Log(LogLevel.Debug,
            $"OutlookCom read complete: {collected.Count} after dedupe, {filtered.Count} within window.");

        return filtered;
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
