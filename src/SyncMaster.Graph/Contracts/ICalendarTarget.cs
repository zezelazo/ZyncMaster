using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SyncMaster.Graph;

public interface ICalendarTarget
{
    Task<IReadOnlyList<CalendarTargetInfo>> ListCalendarsAsync(CancellationToken ct = default);

    Task<CalendarTargetInfo> CreateCalendarAsync(string name, CancellationToken ct = default);

    // Returns a map from externalId → existing event metadata for events found in the calendar.
    Task<IReadOnlyDictionary<string, ExistingEventLookup>> FindByExternalIdsAsync(
        string                  calendarId,
        IReadOnlyList<string>   externalIds,
        CancellationToken       ct = default);

    Task<string> CreateEventAsync(string calendarId, EventDraft draft, CancellationToken ct = default);

    Task UpdateEventAsync(string eventId, EventDraft draft, CancellationToken ct = default);

    Task DeleteEventAsync(string eventId, CancellationToken ct = default);
}
