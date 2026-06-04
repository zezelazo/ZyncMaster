using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ZyncMaster.Graph;

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

    // Lists every event in the calendar whose window overlaps [fromUtc, toUtc] that
    // carries the CalImport source-id extended property, returning its source id and
    // Graph event id. Used to detect managed events that no longer appear in the payload.
    Task<IReadOnlyList<ManagedEventRef>> ListManagedInWindowAsync(
        string            calendarId,
        DateTimeOffset    fromUtc,
        DateTimeOffset    toUtc,
        CancellationToken ct = default);

    // Lists every event in the calendar — across the WHOLE calendar, no window — that carries the
    // CalImportPairId managed property equal to pairId, i.e. exactly the events the given sync pair
    // created. Used by the destination-cleanup path when a pair is re-targeted, to remove the pair's
    // orphaned events from its PREVIOUS destination. Events without the property (the user's own, or
    // another pair's) are never matched, so cleanup can never touch them.
    //
    // A default empty result is provided so the many target test doubles that never exercise the
    // pair cleanup do not have to implement it; GraphCalendarTarget overrides it with the real query.
    Task<IReadOnlyList<ManagedEventRef>> ListManagedByPairAsync(
        string            calendarId,
        string            pairId,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ManagedEventRef>>(Array.Empty<ManagedEventRef>());
}
