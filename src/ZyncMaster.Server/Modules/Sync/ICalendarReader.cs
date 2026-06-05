using ZyncMaster.Core;

namespace ZyncMaster.Server;

// Reads every event in [fromUtc, toUtc] from a source calendar, mapped to the shared
// AppointmentRecord shape. Only providers that can read on the server (e.g. Microsoft
// Graph) expose a reader; an OutlookCom source has no server reader because its events
// arrive via the push endpoint from a desktop device.
public interface ICalendarReader
{
    // Enumerates every calendar of the SOURCE account this reader is bound to. Used by the
    // "All calendars" source mode so the run reads from the ORIGIN account's calendars (the
    // reader is resolved against pair.Source), never the destination's. Mirrors the writer's
    // ListCalendarsAsync shape but is anchored to the source account.
    Task<IReadOnlyList<CalendarOption>> ListCalendarsAsync(CancellationToken ct = default);

    // preserveLocalTime controls how the event's clock time is projected onto
    // AppointmentRecord.Start:
    //   * false (default — the SYNC/mirror path): events are normalized to UTC, so
    //     Start carries the UTC instant. This is what the destructive mirror needs to
    //     reconcile against UTC-normalized destination events. Do NOT change this for sync.
    //   * true (the EXPORT-to-.txt path): events keep the LOCAL wall-clock time the user
    //     sees in their calendar (the event's declared time zone), so the .txt matches what
    //     CalExport's COM exporter renders. Used only when producing the human-facing .txt.
    Task<IReadOnlyList<AppointmentRecord>> ReadWindowAsync(
        string calendarId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct = default,
        bool preserveLocalTime = false);
}
