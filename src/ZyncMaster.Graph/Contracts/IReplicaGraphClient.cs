using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ZyncMaster.Graph;

// Per-account Graph client for the calendar v2 replica engine. Resolved through the same
// account-keyed factory pattern as ICalendarTarget; one instance per (account, run).
public interface IReplicaGraphClient
{
    Task<IReadOnlyList<CalendarTargetInfo>> ListCalendarsAsync(CancellationToken ct = default);

    // Single-event probe (propagation / respond preflight). Null on 404 = origin gone.
    Task<SourceEventSnapshot?> GetEventAsync(string eventId, CancellationToken ct = default);

    // Window read with the three managed marks expanded (replica / rule-processed / CalImport).
    // Events come back normalized to UTC (Prefer outlook.timezone) so ContentHash is stable.
    Task<IReadOnlyList<SourceEventSnapshot>> ListWindowAsync(
        string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        CancellationToken ct = default);

    // Creates the whitelist replica event (ZmReplicaOf mark included). Returns the event id.
    Task<string> CreateReplicaAsync(string calendarId, ReplicaDraft draft, CancellationToken ct = default);

    // Propagation PATCH: start/end/isAllDay/showAs ONLY — never the subject (the user's mask).
    Task UpdateReplicaTimesAsync(string eventId, ReplicaDraft draft, CancellationToken ct = default);

    // Subject-only PATCH: rule strip-rename on the origin, or a mask-title edit on a replica.
    Task UpdateSubjectAsync(string eventId, string subject, CancellationToken ct = default);

    // Stamps ZmRuleProcessed = ruleId so a prefix rule fires exactly once per event.
    Task StampRuleProcessedAsync(string eventId, string ruleId, CancellationToken ct = default);

    // DELETE; a 404 is swallowed (an already-gone replica is a no-op, not an error).
    Task DeleteEventAsync(string eventId, CancellationToken ct = default);

    // The user's own event created from our UI (spec §4): body/location go HERE only.
    Task<string> CreateOriginEventAsync(string calendarId, OriginEventDraft draft, CancellationToken ct = default);

    // One paginated read per destination calendar listing OUR replicas in the window —
    // filtered by ZmReplicaOf exclusively (engine separation, spec §7).
    Task<IReadOnlyList<ReplicaEventRef>> ListReplicasInWindowAsync(
        string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        CancellationToken ct = default);
}
