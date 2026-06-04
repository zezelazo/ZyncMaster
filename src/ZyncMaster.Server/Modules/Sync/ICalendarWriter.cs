using ZyncMaster.Core;

namespace ZyncMaster.Server;

// Mirrors a set of appointment records onto a destination calendar over a window and
// returns the resulting counts. The only writer is Microsoft Graph; OutlookCom is never
// a destination in this milestone.
public interface ICalendarWriter
{
    Task<IReadOnlyList<CalendarOption>> ListCalendarsAsync(CancellationToken ct = default);

    // Creates a new calendar in the account and returns the created calendar. Used by the
    // per-account "+ New calendar" surface so a sync destination can be a fresh calendar.
    Task<CalendarOption> CreateCalendarAsync(string name, CancellationToken ct = default);

    Task<MirrorResult> MirrorAsync(
        string calendarId,
        IReadOnlyList<AppointmentRecord> records,
        int reminderMinutes,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct = default,
        string pairId = "");

    // Deletes from `calendarId` EXACTLY the events the given sync pair created (those carrying the
    // CalImportPairId managed property == pairId), across the whole calendar. Used when a pair is
    // re-targeted to a new destination, to remove the pair's now-orphaned events from its PREVIOUS
    // destination. Best-effort: per-event delete failures are accumulated, not thrown, so a retry
    // can re-enumerate (already-deleted events no longer appear). Events without the property — the
    // user's own events, or another pair's — are never enumerated and so never deleted.
    //
    // A default no-op is provided so the many writer test doubles that never exercise cleanup do
    // not have to implement it; the real MicrosoftGraphProvider overrides it.
    Task<CleanupResult> CleanupManagedAsync(
        string calendarId,
        string pairId,
        CancellationToken ct = default)
        => Task.FromResult(new CleanupResult());

    // Counts (without deleting) the events the given pair created in `calendarId`. Drives the
    // wizard's "Also remove the N events already copied to the previous destination" confirm so the
    // user sees how many events the opt-in cleanup would delete before committing. Default 0 for
    // the test doubles that do not exercise it; MicrosoftGraphProvider overrides it.
    Task<int> CountManagedAsync(
        string calendarId,
        string pairId,
        CancellationToken ct = default)
        => Task.FromResult(0);
}

// Counts from a destination cleanup run (delete-only). Mirrors the shape of MirrorResult but
// scoped to the destructive removal of one pair's managed events from a calendar.
public sealed record CleanupResult
{
    public int Deleted { get; init; }
    public List<string> Failures { get; init; } = new();
}

// A calendar choice surfaced to the panel. Mirrors Graph's CalendarTargetInfo but kept
// in the Server namespace so the API layer never leaks the Graph type.
public sealed record CalendarOption
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public bool IsDefault { get; init; }
    public string Owner { get; init; } = "";
}
