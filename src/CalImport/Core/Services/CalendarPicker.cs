using System;
using System.Collections.Generic;
using System.Linq;
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

    public CalendarTargetInfo Choose(
        ParsedImportArguments         args,
        ImportSettings                settings,
        IReadOnlyList<CalendarTargetInfo> calendars)
    {
        if (args      == null) throw new ArgumentNullException(nameof(args));
        if (settings  == null) throw new ArgumentNullException(nameof(settings));
        if (calendars == null) throw new ArgumentNullException(nameof(calendars));

        if (calendars.Count == 0)
        {
            _terminator.ExitWithError("No calendars found in the account.");
            throw new InvalidOperationException("Unreachable");
        }

        if (!string.IsNullOrWhiteSpace(args.CalendarId))
        {
            var match = calendars.FirstOrDefault(c =>
                string.Equals(c.Id, args.CalendarId, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                _terminator.ExitWithError($"Calendar id '{args.CalendarId}' not found in the account.");
                throw new InvalidOperationException("Unreachable");
            }
            return match;
        }

        if (!string.IsNullOrWhiteSpace(settings.DefaultCalendarId))
        {
            var match = calendars.FirstOrDefault(c =>
                string.Equals(c.Id, settings.DefaultCalendarId, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                return match;

            if (args.AutoMode)
            {
                _terminator.ExitWithError(
                    $"Default calendar id '{settings.DefaultCalendarId}' from settings was not found in the account.");
                throw new InvalidOperationException("Unreachable");
            }

            // Require explicit confirmation: a silent fallback to the picker could lead the user
            // to pick the wrong calendar without noticing that their configured default is broken.
            _console.WriteLine();
            _console.WriteLine($"=== WARNING: defaultCalendarId '{settings.DefaultCalendarId}' from settings was not found in the account. ===");
            _console.WriteLine("Update or remove DefaultCalendarId in settings to fix this permanently.");
            _console.WriteLine();
            _console.Write("Continue and choose a calendar from the list? [Y/n]: ");
            var answer = _console.ReadLine()?.Trim() ?? "";
            if (answer.Equals("n", StringComparison.OrdinalIgnoreCase) ||
                answer.Equals("no", StringComparison.OrdinalIgnoreCase))
            {
                _terminator.ExitWithError("Aborted because defaultCalendarId in settings is invalid.");
                throw new InvalidOperationException("Unreachable");
            }
        }

        if (args.AutoMode)
        {
            var def = calendars.FirstOrDefault(c => c.IsDefault) ?? calendars[0];
            return def;
        }

        return PromptInteractive(calendars);
    }

    private CalendarTargetInfo PromptInteractive(IReadOnlyList<CalendarTargetInfo> calendars)
    {
        _console.WriteLine();
        _console.WriteLine("Available calendars:");
        for (int i = 0; i < calendars.Count; i++)
        {
            var c = calendars[i];
            var marker = c.IsDefault ? " (default)" : "";
            _console.WriteLine($"  {i + 1,2}. {c.DisplayName}{marker}");
        }

        _console.WriteLine();
        _console.Write($"Your choice (1-{calendars.Count}): ");
        var input = _console.ReadLine()?.Trim() ?? "";

        if (int.TryParse(input, out int n) && n >= 1 && n <= calendars.Count)
            return calendars[n - 1];

        _terminator.ExitWithError("Invalid selection.");
        throw new InvalidOperationException("Unreachable");
    }
}
