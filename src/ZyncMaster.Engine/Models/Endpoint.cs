using System.Collections.Generic;

namespace ZyncMaster.Engine;

// One side of a sync pair. Provider is "OutlookCom" (read locally via Outlook COM)
// or "MicrosoftGraph" (read/write through the server's Graph connection).
public sealed record Endpoint
{
    public string Provider { get; init; } = "";
    public string? AccountRef { get; init; }
    public string CalendarId { get; init; } = "";
    public string CalendarName { get; init; } = "";

    // Source-only multi-calendar selection (Feature 2). Back-compat: all three are null/false by
    // default, which means "legacy single calendar" — read exactly CalendarId (Graph) or the
    // device's configured EngineSettings.CalendarNames (COM). They only ever apply to a pair's
    // SOURCE; the destination is always a single CalendarId and ignores them.
    //
    //   AllCalendars   — true => read EVERY calendar of the source account/origin.
    //                    COM: maps to "all"; Graph: enumerate all calendars and read each.
    //   CalendarIds    — Graph: the subset of calendarIds to read (when AllCalendars is false).
    //   CalendarNames  — COM: the subset of calendar display names to read (when AllCalendars is false).
    //
    // Resolution precedence on the SOURCE: AllCalendars wins; else the typed list (CalendarIds for
    // Graph, CalendarNames for COM) when it has items; else legacy single CalendarId / settings.
    public bool AllCalendars { get; init; }
    public IReadOnlyList<string>? CalendarIds { get; init; }
    public IReadOnlyList<string>? CalendarNames { get; init; }
}
