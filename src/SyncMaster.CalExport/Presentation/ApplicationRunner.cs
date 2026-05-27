using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SyncMaster.Core;

namespace SyncMaster.CalExport;

public sealed class ApplicationRunner
{
    private readonly IConsoleIO                       _console;
    private readonly ICalendarService                 _calendarService;
    private readonly ISettingsRepository<AppSettings> _settingsRepo;
    private readonly IFileSystem                      _fileSystem;
    private readonly SettingsResolver         _resolver;
    private readonly CalendarFolderMatcher    _folderMatcher;
    private readonly OutputDirectoryService   _outputDirService;
    private readonly AppointmentExportService _exportService;
    private readonly IApplicationTerminator   _terminator;
    private readonly string                   _exeDir;

    public ApplicationRunner(
        IConsoleIO                       console,
        ICalendarService                 calendarService,
        ISettingsRepository<AppSettings> settingsRepository,
        IFileSystem                      fileSystem,
        SettingsResolver         settingsResolver,
        CalendarFolderMatcher    folderMatcher,
        OutputDirectoryService   outputDirService,
        AppointmentExportService exportService,
        IApplicationTerminator   terminator,
        string                   exeDir)
    {
        _console          = console            ?? throw new ArgumentNullException(nameof(console));
        _calendarService  = calendarService    ?? throw new ArgumentNullException(nameof(calendarService));
        _settingsRepo     = settingsRepository ?? throw new ArgumentNullException(nameof(settingsRepository));
        _fileSystem       = fileSystem         ?? throw new ArgumentNullException(nameof(fileSystem));
        _resolver         = settingsResolver   ?? throw new ArgumentNullException(nameof(settingsResolver));
        _folderMatcher    = folderMatcher      ?? throw new ArgumentNullException(nameof(folderMatcher));
        _outputDirService = outputDirService   ?? throw new ArgumentNullException(nameof(outputDirService));
        _exportService    = exportService      ?? throw new ArgumentNullException(nameof(exportService));
        _terminator       = terminator         ?? throw new ArgumentNullException(nameof(terminator));
        _exeDir           = exeDir             ?? throw new ArgumentNullException(nameof(exeDir));
    }

    public void Run(ParsedArguments args)
    {
        if (args == null) throw new ArgumentNullException(nameof(args));

        var (settingsPath, settings, pendingCreatePath) = ResolveSettingsFile(args);

        var requestedOutput = !string.IsNullOrWhiteSpace(args.OutputPath)
            ? args.OutputPath
            : settings.OutputPath;

        var outputDir = _outputDirService.Resolve(requestedOutput, _exeDir, args.AutoMode);

        string? settingsOutputPath = !string.IsNullOrWhiteSpace(args.OutputPath)
            ? Path.GetFullPath(args.OutputPath)
            : settings.OutputPath;

        if (args.AutoMode)
            RunAutoMode(settings, settingsPath, pendingCreatePath, outputDir, settingsOutputPath);
        else if (pendingCreatePath != null)
            RunNewConfigFlow(settings, settingsPath, pendingCreatePath, outputDir, settingsOutputPath);
        else
            RunNormalFlow(settings, settingsPath, outputDir, settingsOutputPath);
    }

    // ── Settings resolution ───────────────────────────────────────────────

    private (string path, AppSettings settings, string? pendingCreatePath) ResolveSettingsFile(ParsedArguments args)
    {
        var defaultSettingsPath = Path.Combine(_exeDir, "settings.json");

        if (args.ConfigPath != null)
        {
            var fullCustomPath = Path.GetFullPath(args.ConfigPath);

            if (!_fileSystem.FileExists(fullCustomPath))
            {
                var settings = _settingsRepo.LoadOrCreateDefault(defaultSettingsPath);
                return (defaultSettingsPath, settings, fullCustomPath);
            }

            var loaded = _settingsRepo.TryLoad(fullCustomPath);
            if (loaded != null)
                return (fullCustomPath, loaded, null);

            _console.WriteError($"Error: Could not parse settings from '{fullCustomPath}'.");
            _console.WriteLine();
            _console.WriteLine("Example settings.json:");
            _console.WriteLine(JsonConvert.SerializeObject(new AppSettings(), Formatting.Indented));
            _console.WriteLine();
            _console.Write("Continue with default settings? [Y/n]: ");
            var cont = _console.ReadLine()?.Trim() ?? "";
            if (cont.Length > 0 && !cont.Equals("y", StringComparison.OrdinalIgnoreCase))
                _terminator.Exit(0);

            _console.WriteLine("Using default settings instead.");
            var fallback = _settingsRepo.LoadOrCreateDefault(defaultSettingsPath);
            return (defaultSettingsPath, fallback, null);
        }

        {
            var settings = _settingsRepo.LoadOrCreateDefault(defaultSettingsPath);
            return (defaultSettingsPath, settings, null);
        }
    }

    // ── Helpers — resolve defaults (F1) ──────────────────────────────────

    private (int year, int month, ExportMode mode, bool inclCancelled, string[]? calNames) ResolveDefaults(AppSettings settings)
    {
        return (
            _resolver.ResolveYear(settings),
            _resolver.ResolveMonth(settings),
            _resolver.ResolveMode(settings),
            settings.IncludeCancelled,
            _resolver.ResolveCalendarNames(settings));
    }

    // ── Auto mode ────────────────────────────────────────────────────────

    private void RunAutoMode(
        AppSettings settings,
        string      settingsPath,
        string?     pendingCreatePath,
        string      outputDir,
        string?     settingsOutputPath)
    {
        var (year, month, mode, inclCancelled, calNames) = ResolveDefaults(settings);

        _console.WriteLine($"Auto mode — settings: {Path.GetFileName(settingsPath)}");
        DisplaySettings(
            calendarsLabel: calNames == null ? "All calendars" : string.Join(", ", calNames),
            year:           year,
            month:          month,
            mode:           mode,
            inclCancelled:  inclCancelled,
            outputDir:      outputDir);

        var selectedFolders = calNames == null ? null : ResolveNamedCalendars(calNames);

        if (pendingCreatePath != null)
            WriteSettingsWithDirCreation(
                pendingCreatePath,
                BuildSettings(year, new JValue(month), mode, inclCancelled, selectedFolders, settingsOutputPath),
                interactive: false);

        DoExport(year, month, mode, inclCancelled, selectedFolders, outputDir);
    }

    // ── New-config flow ───────────────────────────────────────────────────

    private void RunNewConfigFlow(
        AppSettings settings,
        string      settingsPath,
        string      pendingCreatePath,
        string      outputDir,
        string?     settingsOutputPath)
    {
        var (defYear, defMonth, defMode, defCancelled, defCalNames) = ResolveDefaults(settings);

        _console.WriteLine($"The settings file '{pendingCreatePath}' does not exist.");
        _console.WriteLine();
        _console.Write("Start with default settings? [Y/n]: ");
        bool tryDefaults = IsYes(_console.ReadLine()?.Trim() ?? "", defaultYes: true);

        int        year;
        int        month;
        ExportMode mode;
        bool       inclCancelled;
        IReadOnlyList<CalendarFolderInfo>? selectedFolders;

        bool useDefaults = false;
        if (tryDefaults)
        {
            _console.WriteLine();
            _console.WriteLine("Default settings:");
            DisplaySettings(
                calendarsLabel: defCalNames == null ? "All calendars" : string.Join(", ", defCalNames),
                year:           defYear,
                month:          defMonth,
                mode:           defMode,
                inclCancelled:  defCancelled,
                outputDir:      outputDir);
            _console.WriteLine();
            _console.Write("These settings look good? [Y/n]: ");
            useDefaults = IsYes(_console.ReadLine()?.Trim() ?? "", defaultYes: true);
        }

        if (useDefaults)
        {
            year          = defYear;
            month         = defMonth;
            mode          = defMode;
            inclCancelled = defCancelled;
            selectedFolders = defCalNames == null ? null : ResolveNamedCalendars(defCalNames);
        }
        else
        {
            _console.WriteLine();
            _console.WriteLine("Connecting to Outlook to list available calendars...");
            var allFolders = GetCalendarFoldersOrExit();
            selectedFolders = PromptCalendarSelection(allFolders);
            year          = PromptYear();
            month         = PromptMonth();
            mode          = PromptMode();
            inclCancelled = PromptIncludeCancelled();
        }

        AskCreatePendingFile(pendingCreatePath, year, month, mode, inclCancelled, selectedFolders, settingsOutputPath);
        DoExport(year, month, mode, inclCancelled, selectedFolders, outputDir);
    }

    // ── Normal flow ───────────────────────────────────────────────────────

    private void RunNormalFlow(
        AppSettings settings,
        string      settingsPath,
        string      outputDir,
        string?     settingsOutputPath)
    {
        var (defYear, defMonth, defMode, defCancelled, defCalNames) = ResolveDefaults(settings);

        _console.WriteLine($"Default settings ({Path.GetFileName(settingsPath)}):");
        DisplaySettings(
            calendarsLabel: defCalNames == null ? "All calendars" : string.Join(", ", defCalNames),
            year:           defYear,
            month:          defMonth,
            mode:           defMode,
            inclCancelled:  defCancelled,
            outputDir:      outputDir);
        _console.WriteLine();
        _console.Write("Proceed with these settings? [Y/n]: ");

        bool useDefaults = IsYes(_console.ReadLine()?.Trim() ?? "", defaultYes: true);

        int        year;
        int        month;
        ExportMode mode;
        bool       inclCancelled;
        IReadOnlyList<CalendarFolderInfo>? selectedFolders;

        if (useDefaults)
        {
            year          = defYear;
            month         = defMonth;
            mode          = defMode;
            inclCancelled = defCancelled;
            selectedFolders = defCalNames == null ? null : ResolveNamedCalendars(defCalNames);
        }
        else
        {
            _console.WriteLine();
            _console.WriteLine("Connecting to Outlook to list available calendars...");
            var allFolders = GetCalendarFoldersOrExit();
            selectedFolders = PromptCalendarSelection(allFolders);
            year          = PromptYear();
            month         = PromptMonth();
            mode          = PromptMode();
            inclCancelled = PromptIncludeCancelled();

            AskSaveDefaults(year, month, mode, inclCancelled, selectedFolders, settingsPath, settingsOutputPath);
        }

        DoExport(year, month, mode, inclCancelled, selectedFolders, outputDir);
    }

    // ── Calendar selection ────────────────────────────────────────────────

    private IReadOnlyList<CalendarFolderInfo>? PromptCalendarSelection(IReadOnlyList<CalendarFolderInfo> folders)
    {
        _console.WriteLine();
        _console.WriteLine("Available calendars:");
        _console.WriteLine("   0. All calendars");
        for (int i = 0; i < folders.Count; i++)
            _console.WriteLine($"  {i + 1,2}. {folders[i].DisplayName}");

        _console.WriteLine();
        _console.Write("Your choice (0 = all, or comma-separated numbers e.g. 1,3): ");
        var input = _console.ReadLine()?.Trim() ?? "";

        if (input.Length == 0 || input == "0" || input.Equals("all", StringComparison.OrdinalIgnoreCase))
            return null;

        var selected = new List<CalendarFolderInfo>();
        foreach (var token in input.Split(','))
        {
            var t = token.Trim();
            if (!int.TryParse(t, out int n) || n < 1 || n > folders.Count)
            {
                _terminator.ExitWithError($"Error: '{t}' is not a valid calendar number (1-{folders.Count}).");
            }
            var folder = folders[n - 1];
            if (!selected.Any(f => f.EntryId == folder.EntryId))
                selected.Add(folder);
        }
        return selected;
    }

    // ── Prompts ───────────────────────────────────────────────────────────

    private int PromptYear()
    {
        int current  = DateTime.Today.Year;
        int previous = current - 1;
        _console.WriteLine("\nSelect year:");
        _console.WriteLine($"  1. {previous}");
        _console.WriteLine($"  2. {current}");
        _console.Write("Your choice: ");
        var input = _console.ReadLine()?.Trim();
        if (input == "1") return previous;
        if (input == "2") return current;
        _terminator.ExitWithError("Error: Invalid selection. Enter 1 or 2.");
        throw new InvalidOperationException("Unreachable");
    }

    private int PromptMonth()
    {
        _console.WriteLine("\nSelect month:");
        for (int i = 1; i <= 12; i++)
            _console.WriteLine($"  {i,2}. {MonthNames.Get(i)}");
        _console.Write("Your choice (1-12): ");
        var input = _console.ReadLine()?.Trim();
        if (int.TryParse(input, out int m) && m >= 1 && m <= 12) return m;
        _terminator.ExitWithError("Error: Invalid selection. Enter a number from 1 to 12.");
        throw new InvalidOperationException("Unreachable");
    }

    private ExportMode PromptMode()
    {
        _console.WriteLine("\nSelect export mode:");
        _console.WriteLine("  1. Simple   — TXT file: date, time, duration, title, organizer");
        _console.WriteLine("  2. Complete — JSON file: all fields + description, timezone and participants with response status");
        _console.Write("Your choice (1 or 2): ");
        var input = _console.ReadLine()?.Trim();
        if (input == "1") return ExportMode.Simple;
        if (input == "2") return ExportMode.Complete;
        _terminator.ExitWithError("Error: Invalid selection. Enter 1 or 2.");
        throw new InvalidOperationException("Unreachable");
    }

    private bool PromptIncludeCancelled()
    {
        _console.WriteLine("\nInclude cancelled events?");
        _console.WriteLine("  1. No  — active events only");
        _console.WriteLine("  2. Yes — include cancelled (marked with CANCELADO in TXT / isCancelled in JSON)");
        _console.Write("Your choice (1 or 2): ");
        var input = _console.ReadLine()?.Trim();
        if (input == "1") return false;
        if (input == "2") return true;
        _terminator.ExitWithError("Error: Invalid selection. Enter 1 or 2.");
        throw new InvalidOperationException("Unreachable");
    }

    // ── Save settings ────────────────────────────────────────────────────

    private void AskSaveDefaults(
        int                               year,
        int                               month,
        ExportMode                        mode,
        bool                              inclCancelled,
        IReadOnlyList<CalendarFolderInfo>? selectedFolders,
        string                            settingsPath,
        string?                           outputPath)
    {
        _console.WriteLine();
        _console.Write("Save these options as new defaults? [y/N]: ");
        if (!IsYes(_console.ReadLine()?.Trim() ?? "", defaultYes: false))
            return;

        var monthToken = PromptMonthSaveMode(month);
        var toSave = BuildSettings(year, monthToken, mode, inclCancelled, selectedFolders, outputPath);
        WriteSettingsWithDirCreation(settingsPath, toSave, interactive: true);
        _console.WriteLine($"  New defaults saved to {settingsPath}.");
    }

    private void AskCreatePendingFile(
        string                            pendingPath,
        int                               year,
        int                               month,
        ExportMode                        mode,
        bool                              inclCancelled,
        IReadOnlyList<CalendarFolderInfo>? selectedFolders,
        string?                           outputPath)
    {
        _console.WriteLine();
        _console.Write($"Save these settings to '{pendingPath}'? [y/N]: ");
        if (!IsYes(_console.ReadLine()?.Trim() ?? "", defaultYes: false))
            return;

        var monthToken = PromptMonthSaveMode(month);
        WriteSettingsWithDirCreation(
            pendingPath,
            BuildSettings(year, monthToken, mode, inclCancelled, selectedFolders, outputPath),
            interactive: true);
    }

    // ── Month save mode prompt (F2) ───────────────────────────────────────

    private JToken PromptMonthSaveMode(int currentMonth)
    {
        _console.WriteLine();
        _console.WriteLine("How should the month be saved?");
        _console.WriteLine($"  1. Fixed: always {MonthNames.Get(currentMonth)} — saves month {currentMonth} regardless of when you run this");
        _console.WriteLine("  2. Current month — always defaults to whichever month is current when you run this");
        _console.WriteLine("  3. Previous month — always defaults to the month before the current one");
        _console.Write("Your choice (1-3): ");

        return (_console.ReadLine()?.Trim()) switch
        {
            "2" => new JValue("current"),
            "3" => new JValue("previous"),
            _   => new JValue(currentMonth),
        };
    }

    private void WriteSettingsWithDirCreation(string filePath, AppSettings s, bool interactive)
    {
        var dir = Path.GetDirectoryName(filePath) ?? "";
        if (!string.IsNullOrEmpty(dir) && !_fileSystem.DirectoryExists(dir))
        {
            if (interactive)
            {
                _console.Write($"Directory '{dir}' does not exist. Create it? [Y/n]: ");
                if (!IsYes(_console.ReadLine()?.Trim() ?? "", defaultYes: true))
                {
                    _console.WriteLine("  Settings file not saved.");
                    return;
                }
            }

            try
            {
                _fileSystem.CreateDirectory(dir);
                if (interactive) _console.WriteLine($"  Directory created: {dir}");
            }
            catch (UnauthorizedAccessException)
            {
                _console.WriteError($"Error: Access denied creating '{dir}'.");
                if (interactive) _console.WriteLine("  Settings file not saved.");
                return;
            }
            catch (IOException)
            {
                _console.WriteError($"Error: Could not create '{dir}' — invalid path.");
                if (interactive) _console.WriteLine("  Settings file not saved.");
                return;
            }
        }

        try
        {
            _settingsRepo.Save(s, filePath);
            _console.WriteLine($"  Settings file created: {filePath}");
        }
        catch (UnauthorizedAccessException)
        {
            _console.WriteError($"Error: Access denied writing to '{filePath}'.");
        }
        catch (IOException)
        {
            _console.WriteError($"Error: Could not write to '{filePath}' — invalid path.");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static bool IsYes(string input, bool defaultYes = true)
    {
        if (input.Length == 0) return defaultYes;
        return input.Equals("y",   StringComparison.OrdinalIgnoreCase) ||
               input.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static AppSettings BuildSettings(
        int                               year,
        JToken                            monthToken,
        ExportMode                        mode,
        bool                              inclCancelled,
        IReadOnlyList<CalendarFolderInfo>? selectedFolders,
        string?                           outputPath) =>
        new AppSettings
        {
            Year             = new JValue(year),
            Month            = monthToken,
            Mode             = mode == ExportMode.Simple ? "simple" : "complete",
            IncludeCancelled = inclCancelled,
            Calendars        = selectedFolders == null
                ? (JToken)new JValue("all")
                : JArray.FromObject(selectedFolders.Select(f => f.DisplayName).ToList()),
            OutputPath       = string.IsNullOrWhiteSpace(outputPath) ? null : outputPath,
        };

    private IReadOnlyList<CalendarFolderInfo>? ResolveNamedCalendars(string[] names)
    {
        _console.WriteLine();
        _console.WriteLine("Connecting to Outlook to verify selected calendars...");
        var allFolders = GetCalendarFoldersOrExit();
        return _folderMatcher.Match(
            names,
            allFolders,
            onNotFound: name => _console.WriteLine($"  Warning: calendar \"{name}\" not found in Outlook — skipped."));
    }

    private IReadOnlyList<CalendarFolderInfo> GetCalendarFoldersOrExit()
    {
        try
        {
            var folders = _calendarService.GetCalendarFolders();
            if (folders.Count == 0)
            {
                _terminator.ExitWithError("No calendar folders found in Outlook.");
                throw new InvalidOperationException("Unreachable");
            }
            return folders;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            _terminator.ExitWithError($"\nError: {ex.Message}");
            throw new InvalidOperationException("Unreachable");
        }
    }

    private void DisplaySettings(
        string     calendarsLabel,
        int        year,
        int        month,
        ExportMode mode,
        bool       inclCancelled,
        string     outputDir)
    {
        _console.WriteLine($"  Calendars  : {calendarsLabel}");
        _console.WriteLine($"  Year       : {year}");
        _console.WriteLine($"  Month      : {MonthNames.Get(month)}");
        _console.WriteLine($"  Mode       : {mode}");
        _console.WriteLine($"  Cancelled  : {(inclCancelled ? "Included" : "Excluded")}");
        _console.WriteLine($"  Output dir : {outputDir}");
    }

    private void DoExport(
        int                               year,
        int                               month,
        ExportMode                        mode,
        bool                              inclCancelled,
        IReadOnlyList<CalendarFolderInfo>? selectedFolders,
        string                            outputDir)
    {
        var calDesc = selectedFolders == null
            ? "all calendars"
            : string.Join(", ", selectedFolders.Select(f => f.DisplayName));

        _console.WriteLine();
        _console.WriteLine($"Exporting {MonthNames.Get(month)} {year} | {mode} | {calDesc}{(inclCancelled ? " | including cancelled" : "")}...");

        try
        {
            var parameters = new ExportParameters(year, month, mode, inclCancelled, selectedFolders);
            var filePath   = _exportService.Export(parameters, outputDir);
            _console.WriteLine($"Done. File: {filePath}");
        }
        catch (Exception ex)
        {
            _terminator.ExitWithError($"\nError: {ex.Message}");
        }
    }
}
