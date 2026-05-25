using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SyncMaster.Core;

namespace SyncMaster.CalImport;

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

        var settings = LoadSettings(args);
        ValidateSettings(settings);

        var sourcePath = ResolveSourcePath(args);
        var payload    = LoadPayload(sourcePath);

        _console.WriteLine($"Loaded {payload.Events.Count} event(s) from {Path.GetFileName(sourcePath)}.");

        var calendarTarget = _calendarTargetFactory(settings);

        var calendar = ResolveCalendarTarget(args, settings, calendarTarget);
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

        var reminder = _settingsResolver.ResolveReminderMinutes(settings);
        var result   = ExecutePlanAsync(plan, calendar, calendarTarget, reminder).GetAwaiter().GetResult();

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

    private ImportSettings LoadSettings(ParsedImportArguments args)
    {
        var defaultPath = Path.Combine(_exeDir, "settings.json");
        var path        = args.ConfigPath != null ? Path.GetFullPath(args.ConfigPath) : defaultPath;

        if (args.ConfigPath != null && !_fileSystem.FileExists(path))
        {
            _terminator.ExitWithError($"Config file not found: {path}");
            throw new InvalidOperationException("Unreachable");
        }

        return _settingsRepo.LoadOrCreateDefault(path);
    }

    private void ValidateSettings(ImportSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ClientId))
        {
            _console.WriteError("Error: 'clientId' is empty in settings.json.");
            _console.WriteLine();
            _console.WriteLine("Register a public-client app in portal.azure.com:");
            _console.WriteLine("  1. Azure Active Directory > App registrations > New registration");
            _console.WriteLine("  2. Supported account types: 'Personal Microsoft accounts only'");
            _console.WriteLine("  3. Redirect URI: 'Public client/native (mobile & desktop)' → http://localhost");
            _console.WriteLine("  4. API permissions: Microsoft Graph > Delegated > Calendars.ReadWrite, User.Read");
            _console.WriteLine("  5. Authentication > Allow public client flows = Yes");
            _console.WriteLine("  6. Copy the Application (client) ID into 'clientId' in settings.json");
            _terminator.Exit(1);
            throw new InvalidOperationException("Unreachable");
        }
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

    // ── Calendar selection ────────────────────────────────────────────────

    private CalendarTargetInfo ResolveCalendarTarget(
        ParsedImportArguments args,
        ImportSettings        settings,
        ICalendarTarget       target)
    {
        if (!string.IsNullOrWhiteSpace(args.NewCalendarName))
        {
            _console.WriteLine($"Creating new calendar '{args.NewCalendarName}'...");
            return target.CreateCalendarAsync(args.NewCalendarName!).GetAwaiter().GetResult();
        }

        _console.WriteLine("Listing calendars from your account...");
        var calendars = target.ListCalendarsAsync().GetAwaiter().GetResult();
        return _calendarPicker.Choose(args, settings, calendars);
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
        int                           reminderMinutes)
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
                        // ForUpdate factory guarantees ExistingEventId/ExistingBodyHtml are non-null;
                        // a runtime guard (instead of Debug.Assert, which is stripped in Release)
                        // protects against a factory regression instead of crashing with an opaque NRE.
                        // This InvalidOperationException is intentionally not caught below — a broken
                        // invariant should abort the run, not be logged as a per-item failure.
                        if (item.ExistingEventId == null)
                            throw new InvalidOperationException(
                                "ImportPlanItem with Action=Update has null ExistingEventId — factory invariant broken.");
                        if (item.ExistingBodyHtml == null)
                            throw new InvalidOperationException(
                                "ImportPlanItem with Action=Update has null ExistingBodyHtml — factory invariant broken.");
                        var draft = _draftBuilder.BuildForUpdate(item.Record, reminderMinutes, item.ExistingBodyHtml);
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
