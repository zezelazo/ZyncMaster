using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ZyncMaster.Core;

namespace ZyncMaster.Engine;

public interface ICalendarSource
{
    // Reads [fromUtc, toUtc] from the local calendar. calendarNames selects which local Outlook
    // calendars to merge by display name (Feature 2, per-pair selection):
    //   * null  → "all calendars" (the CalendarFolderMatcher / CalExport "all" convention);
    //   * items → read and merge ONLY those calendars (deduped by event Id across calendars/months).
    // Pass the per-pair selection resolved by the caller (PairRunner); a legacy pair with no
    // selection falls back to the device's configured names, resolved by the caller too.
    Task<IReadOnlyList<AppointmentRecord>> ReadWindowAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, IReadOnlyList<string>? calendarNames, CancellationToken ct);
}
