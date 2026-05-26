using System;
using SyncMaster.Graph;

namespace SyncMaster.CalImport;

// The outcome of CalendarPicker: either use an existing calendar or create a new
// one with the given name. The picker stays free of Graph I/O — ApplicationRunner
// performs the actual creation when NewCalendarName is set.
public sealed class CalendarSelection
{
    private CalendarSelection(CalendarTargetInfo? existing, string? newCalendarName)
    {
        Existing        = existing;
        NewCalendarName = newCalendarName;
    }

    public CalendarTargetInfo? Existing        { get; }
    public string?             NewCalendarName { get; }

    public bool IsCreateNew => NewCalendarName != null;

    public static CalendarSelection Use(CalendarTargetInfo existing)
        => new CalendarSelection(existing ?? throw new ArgumentNullException(nameof(existing)), null);

    public static CalendarSelection CreateNew(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Calendar name must not be empty.", nameof(name));
        return new CalendarSelection(null, name);
    }
}
