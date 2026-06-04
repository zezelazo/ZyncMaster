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
        CancellationToken ct = default);
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
