using System;
using System.Collections.Generic;
using SyncMaster.Core;

namespace SyncMaster.CalImport;

public sealed class CalendarPicker
{
    private readonly IConsoleIO             _console;
    private readonly IApplicationTerminator _terminator;

    public CalendarPicker(IConsoleIO console, IApplicationTerminator terminator)
    {
        _console    = console    ?? throw new ArgumentNullException(nameof(console));
        _terminator = terminator ?? throw new ArgumentNullException(nameof(terminator));
    }

    // Interactive list prompt: choose an existing calendar by number, or 'N' to
    // create a new one (prompts for its name). Non-interactive resolution
    // (explicit --calendar / --new-calendar flags, --auto defaults, the saved
    // defaultCalendarId, and the show/confirm/save flow) is handled by
    // ApplicationRunner, mirroring how CalExport keeps that logic in its runner.
    public CalendarSelection PromptSelection(IReadOnlyList<CalendarTargetInfo> calendars)
    {
        if (calendars == null) throw new ArgumentNullException(nameof(calendars));
        if (calendars.Count == 0)
        {
            _terminator.ExitWithError("No calendars found in the account.");
            throw new InvalidOperationException("Unreachable");
        }

        _console.WriteLine();
        _console.WriteLine("Available calendars:");
        for (int i = 0; i < calendars.Count; i++)
        {
            var c = calendars[i];
            var marker = c.IsDefault ? " (default)" : "";
            _console.WriteLine($"  {i + 1,2}. {c.DisplayName}{marker}");
        }
        _console.WriteLine("   N. Create a new calendar");

        _console.WriteLine();
        _console.Write($"Your choice (1-{calendars.Count}, or N): ");
        var input = _console.ReadLine()?.Trim() ?? "";

        if (input.Equals("n", StringComparison.OrdinalIgnoreCase))
            return PromptNewCalendarName();

        if (int.TryParse(input, out int n) && n >= 1 && n <= calendars.Count)
            return CalendarSelection.Use(calendars[n - 1]);

        _terminator.ExitWithError("Invalid selection.");
        throw new InvalidOperationException("Unreachable");
    }

    private CalendarSelection PromptNewCalendarName()
    {
        _console.Write("New calendar name: ");
        var name = _console.ReadLine()?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(name))
        {
            _terminator.ExitWithError("Calendar name is required.");
            throw new InvalidOperationException("Unreachable");
        }

        return CalendarSelection.CreateNew(name);
    }
}
