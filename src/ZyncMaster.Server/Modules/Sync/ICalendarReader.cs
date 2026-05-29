using ZyncMaster.Core;

namespace ZyncMaster.Server;

// Reads every event in [fromUtc, toUtc] from a source calendar, mapped to the shared
// AppointmentRecord shape. Only providers that can read on the server (e.g. Microsoft
// Graph) expose a reader; an OutlookCom source has no server reader because its events
// arrive via the push endpoint from a desktop device.
public interface ICalendarReader
{
    Task<IReadOnlyList<AppointmentRecord>> ReadWindowAsync(
        string calendarId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct = default);
}
