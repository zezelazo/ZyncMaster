using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ZyncMaster.Core;
using ZyncMaster.Graph;

namespace ZyncMaster.CalImport;

public sealed class ApplicationRunner
{
    private readonly IConsoleIO                          _console;
    private readonly IApplicationTerminator              _terminator;
    private readonly IFileSystem                         _fileSystem;
    private readonly ISettingsRepository<ImportSettings> _settingsRepo;
    private readonly ImportSettingsResolver              _settingsResolver;
    private readonly IImportSource                       _importSource;
    private readonly ImportPlanBuilder                   _planBuilder;
    private readonly EventDraftBuilder                   _draftBuilder;
    private readonly CalendarPicker                      _calendarPicker;
    private readonly Func<ImportSettings, ICalendarTarget> _calendarTargetFactory;
    private readonly string                              _exeDir;

    public ApplicationRunner(
        IConsoleIO                            console,
        IApplicationTerminator                terminator,
        IFileSystem                           fileSystem,
        ISettingsRepository<ImportSettings>   settingsRepo,
        ImportSettingsResolver                settingsResolver,
        IImportSource                         importSource,
        ImportPlanBuilder                     planBuilder,
        EventDraftBuilder                     draftBuilder,
        CalendarPicker                        calendarPicker,
        Func<ImportSettings, ICalendarTarget> calendarTargetFactory,
        string                                exeDir)
    {
        _console               = console               ?? throw new ArgumentNullException(nameof(console));
        _terminator            = terminator            ?? throw new ArgumentNullException(nameof(terminator));
        _fileSystem            = fileSystem            ?? throw new ArgumentNullException(nameof(fileSystem));
        _settingsRepo          = settingsRepo          ?? throw new ArgumentNullException(nameof(settingsRepo));
        _settingsResolver      = settingsResolver      ?? throw new ArgumentNullException(nameof(settingsResolver));
        _importSource          = importSource          ?? throw new ArgumentNullException(nameof(importSource));
        _planBuilder           = planBuilder           ?? throw new ArgumentNullException(nameof(planBuilder));
        _draftBuilder          = draftBuilder          ?? throw new ArgumentNullException(nameof(draftBuilder));
        _calendarPicker        = calendarPicker        ?? throw new ArgumentNullException(nameof(calendarPicker));
        _calendarTargetFactory = calendarTargetFactory ?? throw new ArgumentNullException(nameof(calendarTargetFactory));
        _exeDir                = exeDir                ?? throw new ArgumentNullException(nameof(exeDir));
    }

    public void Run(ParsedImportArguments args)
    {
        if (args == null) throw new ArgumentNullException(nameof(args));

        var (settingsPath, settings) = LoadSettings(args);

        var sourcePath = ResolveSourcePath(args);
        var payload    = LoadPayload(sourcePath);

        _console.WriteLine($"Loaded {payload.Events.Count} event(s) from {Path.GetFileName(sourcePath)}.");

        var calendarTarget = _calendarTargetFactory(settings);

        var (calendar, reminderMinutes, overwrite) = ResolveTargetAndReminder(args, settings, settingsPath, calendarTarget);
        _console.WriteLine($"Target calendar: {calendar.DisplayName} ({calendar.Owner})");

        var ids = payload.Events.Select(e => e.Id).Where(x => !string.IsNullOrEmpty(x)).ToList();
        _console.WriteLine($"Searching destination for {ids.Count} existing event(s)...");
        var existing = calendarTarget.FindByExternalIdsAsync(calendar.Id, ids).GetAwaiter().GetResult();
        _console.WriteLine($"Found {existing.Count} match(es).");

        var plan = _planBuilder.Build(payload.Events, existing);
        PrintPlanSummary(plan);

        if (args.DryRun)
        {
            _console.WriteLine();
            _console.WriteLine("--dry-run: no changes applied.");
            _terminator.Exit(0);
            return;
        }

        var result = ExecutePlanAsync(plan, calendar, calendarTarget, reminderMinutes, overwrite).GetAwaiter().GetResult();

        PrintResult(result);

        if (result.Failed.Count > 0)
        {
            _terminator.Exit(2);
            return;
        }

        if (result.Created == 0 && result.Updated == 0 && result.Cancelled == 0)
        {
            _console.WriteLine();
            _console.WriteError(
                $"WARNING: No changes applied. Source had {payload.Events.Count} event(s); all were skipped or empty.");
            _terminator.Exit(3);
            return;
        }

        _terminator.Exit(0);
    }

    // ── Settings ──────────────────────────────────────────────────────────

    private (string path, ImportSettings settings) LoadSettings(ParsedImportArguments args)
    {
        var defaultPath = Path.Combine(_exeDir, "settings.json");
        var path        = args.ConfigPath != null ? Path.GetFullPath(args.ConfigPath) : defaultPath;

        if (args.ConfigPath != null && !_fileSystem.FileExists(path))
        {
            _terminator.ExitWithError($"Config file not found: {path}");
            throw new InvalidOperationException("Unreachable");
        }

        return (path, _settingsRepo.LoadOrCreateDefault(path));
    }

    // ── Source path ───────────────────────────────────────────────────────

    private string ResolveSourcePath(ParsedImportArguments args)
    {
        if (!string.IsNullOrWhiteSpace(args.SourcePath))
            return Path.GetFullPath(args.SourcePath);

        if (args.AutoMode)
        {
            _terminator.ExitWithError("--auto requires -s/--source to be specified.");
            throw new InvalidOperationException("Unreachable");
        }

        _console.Write("Path to the CalExport JSON to import: ");
        var input = _console.ReadLine()?.Trim().Trim('"') ?? "";

        if (string.IsNullOrWhiteSpace(input))
        {
            _terminator.ExitWithError("Source path is required.");
            throw new InvalidOperationException("Unreachable");
        }

        return Path.GetFullPath(input);
    }

    private ImportPayload LoadPayload(string sourcePath)
    {
        try
        {
            return _importSource.Load(sourcePath);
        }
        catch (ImportSourceException ex)
        {
            _terminator.ExitWithError($"Error reading source file: {ex.Message}");
            throw new InvalidOperationException("Unreachable");
        }
    }

    // ── Calendar + reminder resolution (export-style settings flow) ─────────

    private (CalendarTargetInfo calendar, int reminderMinutes, bool overwrite) ResolveTargetAndReminder(
        ParsedImportArguments args,
        ImportSettings        settings,
        string                settingsPath,
        ICalendarTarget       target)
    {
        var reminder = _settingsResolver.ResolveReminderMinutes(settings);

        // Explicit: create a new calendar by name (scripting; no prompts, no save).
        if (!string.IsNullOrWhiteSpace(args.NewCalendarName))
            return (CreateCalendar(target, args.NewCalendarName!), reminder, args.Overwrite);

        _console.WriteLine("Listing calendars from your account...");
        var calendars = target.ListCalendarsAsync().GetAwaiter().GetResult();
        if (calendars.Count == 0)
        {
            _terminator.ExitWithError("No calendars found in the account.");
            throw new InvalidOperationException("Unreachable");
        }

        // Explicit: use a calendar by id (scripting; no prompts, no save).
        if (!string.IsNullOrWhiteSpace(args.CalendarId))
        {
            var match = FindById(calendars, args.CalendarId!);
            if (match == null)
            {
                _terminator.ExitWithError($"Calendar id '{args.CalendarId}' not found in the account.");
                throw new InvalidOperationException("Unreachable");
            }
            return (match, reminder, args.Overwrite);
        }

        var savedDefault = !string.IsNullOrWhiteSpace(settings.DefaultCalendarId)
            ? FindById(calendars, settings.DefaultCalendarId!)
            : null;

        // Auto mode: use defaults, no prompts, no save.
        if (args.AutoMode)
        {
            if (!string.IsNullOrWhiteSpace(settings.DefaultCalendarId) && savedDefault == null)
            {
                _terminator.ExitWithError(
                    $"Default calendar id '{settings.DefaultCalendarId}' from settings was not found in the account.");
                throw new InvalidOperationException("Unreachable");
            }
            var auto = savedDefault ?? calendars.FirstOrDefault(c => c.IsDefault) ?? calendars[0];
            return (auto, reminder, args.Overwrite);
        }

        // Interactive: a valid saved default shows the settings and offers to proceed.
        if (savedDefault != null)
        {
            DisplaySettings(settings, savedDefault, reminder);
            _console.Write("Proceed with these settings? [Y/n]: ");
            if (IsYes(_console.ReadLine(), defaultYes: true))
                return (savedDefault, reminder, args.Overwrite);
        }
        else if (!string.IsNullOrWhiteSpace(settings.DefaultCalendarId))
        {
            _console.WriteLine();
            _console.WriteLine($"=== WARNING: defaultCalendarId '{settings.DefaultCalendarId}' from settings was not found in the account. ===");
            _console.WriteLine("It will be ignored; choose a calendar below.");
        }

        // Interactive selection: pick an existing calendar or create a new one.
        var selection = _calendarPicker.PromptSelection(calendars);
        var calendar  = selection.IsCreateNew
            ? CreateCalendar(target, selection.NewCalendarName!)
            : selection.Existing!;

        reminder = PromptReminder(reminder);

        var overwrite = args.Overwrite || PromptOverwrite();

        AskSaveDefaults(settings, settingsPath, calendar, reminder);

        return (calendar, reminder, overwrite);
    }

    private bool PromptOverwrite()
    {
        // Per-run choice (not persisted): forcing overwrite every run would silently
        // clobber descriptions the user edited in Outlook. Only affects Update actions.
        _console.Write("Overwrite existing event descriptions from the file? [y/N]: ");
        return IsYes(_console.ReadLine(), defaultYes: false);
    }

    private CalendarTargetInfo CreateCalendar(ICalendarTarget target, string name)
    {
        _console.WriteLine($"Creating new calendar '{name}'...");
        return target.CreateCalendarAsync(name).GetAwaiter().GetResult();
    }

    private static CalendarTargetInfo? FindById(IReadOnlyList<CalendarTargetInfo> calendars, string id)
        => calendars.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    private void DisplaySettings(ImportSettings settings, CalendarTargetInfo calendar, int reminder)
    {
        _console.WriteLine();
        _console.WriteLine("Current settings (settings.json):");
        _console.WriteLine($"  Account  : {(string.IsNullOrWhiteSpace(settings.AccountHint) ? "(sign-in account)" : settings.AccountHint)}");
        _console.WriteLine($"  Calendar : {calendar.DisplayName}");
        _console.WriteLine($"  Reminder : {reminder} min before");
        _console.WriteLine();
    }

    private int PromptReminder(int current)
    {
        _console.Write($"Reminder minutes before each event [{current}]: ");
        var input = _console.ReadLine()?.Trim() ?? "";
        if (input.Length == 0)
            return current;
        if (int.TryParse(input, out int m) && m >= 0)
            return m;

        _console.WriteLine($"  Invalid number; keeping {current}.");
        return current;
    }

    private void AskSaveDefaults(ImportSettings settings, string settingsPath, CalendarTargetInfo calendar, int reminder)
    {
        _console.Write("Save this calendar and reminder as defaults? [y/N]: ");
        if (!IsYes(_console.ReadLine(), defaultYes: false))
            return;

        settings.DefaultCalendarId = calendar.Id;
        settings.ReminderMinutes   = reminder;

        try
        {
            _settingsRepo.Save(settings, settingsPath);
            _console.WriteLine($"  Defaults saved to {settingsPath}.");
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            _console.WriteError($"  Could not save settings: {ex.Message}");
        }
    }

    private static bool IsYes(string? input, bool defaultYes)
    {
        var s = input?.Trim() ?? "";
        if (s.Length == 0) return defaultYes;
        return s.Equals("y", StringComparison.OrdinalIgnoreCase) ||
               s.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    // ── Plan execution ────────────────────────────────────────────────────

    private void PrintPlanSummary(IReadOnlyList<ImportPlanItem> plan)
    {
        var byAction = plan.GroupBy(p => p.Action).ToDictionary(g => g.Key, g => g.Count());
        _console.WriteLine();
        _console.WriteLine("Plan:");
        _console.WriteLine($"  Create : {byAction.GetValueOrDefaultSafe(ImportAction.Create)}");
        _console.WriteLine($"  Update : {byAction.GetValueOrDefaultSafe(ImportAction.Update)}");
        _console.WriteLine($"  Cancel : {byAction.GetValueOrDefaultSafe(ImportAction.Cancel)}");
        _console.WriteLine($"  Skip   : {byAction.GetValueOrDefaultSafe(ImportAction.Skip)}");
    }

    private async Task<ImportResult> ExecutePlanAsync(
        IReadOnlyList<ImportPlanItem> plan,
        CalendarTargetInfo            calendar,
        ICalendarTarget               target,
        int                           reminderMinutes,
        bool                          overwrite)
    {
        var result = new ImportResult();
        int n      = 0;
        foreach (var item in plan)
        {
            n++;
            var label = $"[{n}/{plan.Count}] {item.Record.Start:yyyy-MM-dd HH:mm} {item.Record.Subject}";
            try
            {
                switch (item.Action)
                {
                    case ImportAction.Create:
                    {
                        var draft = _draftBuilder.BuildForCreate(item.Record, reminderMinutes);
                        await target.CreateEventAsync(calendar.Id, draft).ConfigureAwait(false);
                        _console.WriteLine($"  CREATE {label}");
                        result.Created++;
                        break;
                    }
                    case ImportAction.Update:
                    {
                        // ForUpdate/ForCancel factories guarantee ExistingEventId is non-null;
                        // a runtime guard (instead of Debug.Assert, which is stripped in Release)
                        // protects against a factory regression instead of crashing with an opaque NRE.
                        // This InvalidOperationException is intentionally not caught below — a broken
                        // invariant should abort the run, not be logged as a per-item failure.
                        if (item.ExistingEventId == null)
                            throw new InvalidOperationException(
                                "ImportPlanItem with Action=Update has null ExistingEventId — factory invariant broken.");

                        EventDraft draft;
                        if (overwrite)
                        {
                            // Force: rebuild the body from the file (description + participants),
                            // replacing whatever is in the destination event.
                            draft = _draftBuilder.BuildForCreate(item.Record, reminderMinutes);
                        }
                        else
                        {
                            if (item.ExistingBodyHtml == null)
                                throw new InvalidOperationException(
                                    "ImportPlanItem with Action=Update has null ExistingBodyHtml — factory invariant broken.");
                            // Default: preserve the destination body, only refresh the participants block.
                            draft = _draftBuilder.BuildForUpdate(item.Record, reminderMinutes, item.ExistingBodyHtml);
                        }

                        await target.UpdateEventAsync(item.ExistingEventId, draft).ConfigureAwait(false);
                        _console.WriteLine($"  UPDATE {label}");
                        result.Updated++;
                        break;
                    }
                    case ImportAction.Cancel:
                    {
                        // ForCancel factory guarantees ExistingEventId is non-null. See note above
                        // for why this is a runtime guard rather than Debug.Assert.
                        if (item.ExistingEventId == null)
                            throw new InvalidOperationException(
                                "ImportPlanItem with Action=Cancel has null ExistingEventId — factory invariant broken.");
                        await target.DeleteEventAsync(item.ExistingEventId).ConfigureAwait(false);
                        _console.WriteLine($"  CANCEL {label}");
                        result.Cancelled++;
                        break;
                    }
                    case ImportAction.Skip:
                        _console.WriteLine($"  SKIP   {label}");
                        result.Skipped++;
                        break;
                }
            }
            // Cancellation aborts the entire run; do not swallow per-item.
            catch (OperationCanceledException)
            {
                throw;
            }
            // Global authentication failure means subsequent items will also fail; abort
            // instead of producing N identical "auth failed" lines.
            catch (AuthenticationFailedException)
            {
                throw;
            }
            // Per-item Graph failure (HTTP error after retries, malformed response, etc.):
            // log and continue with the next item.
            catch (GraphRequestException ex)
            {
                _console.WriteLine($"  FAIL   {label}: {ex.Message}");
                result.AddFailure($"{item.Record.Id}: {ex.Message}");
            }
        }
        return result;
    }

    private void PrintResult(ImportResult result)
    {
        _console.WriteLine();
        _console.WriteLine("Result:");
        _console.WriteLine($"  Created : {result.Created}");
        _console.WriteLine($"  Updated : {result.Updated}");
        _console.WriteLine($"  Cancelled: {result.Cancelled}");
        _console.WriteLine($"  Skipped : {result.Skipped}");
        _console.WriteLine($"  Failed  : {result.Failed.Count}");
        foreach (var f in result.Failed)
            _console.WriteLine($"    - {f}");
    }
}

internal static class DictionaryExtensions
{
    public static int GetValueOrDefaultSafe<TKey>(this IDictionary<TKey, int> dict, TKey key)
        => dict.TryGetValue(key, out var v) ? v : 0;
}
