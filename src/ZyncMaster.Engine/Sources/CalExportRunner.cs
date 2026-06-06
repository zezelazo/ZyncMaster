using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ZyncMaster.Core;

namespace ZyncMaster.Engine;

// Drives the headless ZyncMaster.CalExport.exe to produce a single month's Complete-mode JSON.
//
// This is an untested process boundary (like OutlookCalendarService): it shells out
// to an external executable and reads its file output. It is consumed through
// ICalExportRunner so OutlookComSource can be unit-tested with a fake runner.
public sealed class CalExportRunner : ICalExportRunner
{
    // Fallback ceiling when the caller passes a non-positive timeout. Outlook's modal prompts
    // (Programmatic Access "Allow access", a corrupt profile, an MFA wall) can block the headless
    // child forever; this bounds the wait so the scheduler is never wedged by a hung child.
    public const int DefaultTimeoutMinutes = 5;

    private readonly string _calExportExePath;
    private readonly IAppLogger _logger;
    private readonly TimeSpan _timeout;

    public CalExportRunner(string calExportExePath, IAppLogger? logger = null, int timeoutMinutes = DefaultTimeoutMinutes)
    {
        _calExportExePath = calExportExePath ?? throw new ArgumentNullException(nameof(calExportExePath));
        _logger = logger ?? NullAppLogger.Instance;
        _timeout = TimeSpan.FromMinutes(timeoutMinutes <= 0 ? DefaultTimeoutMinutes : timeoutMinutes);
    }

    public async Task<string> ExportMonthAsync(int year, int month, IReadOnlyList<string>? calendarNames, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "calexport_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.json");

        try
        {
            var config = BuildConfig(year, month, "complete", calendarNames, includeCancelled: true, outputPath: null);
            await File.WriteAllTextAsync(configPath, config.ToString(Formatting.Indented), ct);

            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.Log(LogLevel.Debug,
                    $"CalExport export (complete): year={year} month={month} mode=complete includeCancelled=true calendars={DescribeCalendars(calendarNames)}");

            await RunProcessAsync(configPath, tempDir, ct);

            var produced = Directory
                .EnumerateFiles(tempDir, "*.json")
                .Where(f => !string.Equals(f, configPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (produced.Count == 0)
            {
                _logger.Log(LogLevel.Error, $"CalExport produced no JSON output file (year={year} month={month}).");
                throw new InvalidOperationException("CalExport produced no JSON output file.");
            }

            _logger.Log(LogLevel.Debug, $"CalExport produced {produced.Count} JSON file(s) for {year}-{month:D2}.");
            return await File.ReadAllTextAsync(produced[0], ct);
        }
        finally
        {
            TryDelete(tempDir);
        }
    }

    public async Task ExportSimpleAsync(int year, int month, IReadOnlyList<string>? calendarNames, bool includeCancelled, string outputFilePath, CancellationToken ct)
    {
        if (outputFilePath == null) throw new ArgumentNullException(nameof(outputFilePath));

        var tempDir = Path.Combine(Path.GetTempPath(), "calexport_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.json");

        try
        {
            // CalExport ignores the config outputPath as a file target — it always writes an
            // auto-named file into the -o directory. So we run into the temp dir, then move
            // the produced .txt to the caller's requested path.
            var config = BuildConfig(year, month, "simple", calendarNames, includeCancelled, outputPath: null);
            await File.WriteAllTextAsync(configPath, config.ToString(Formatting.Indented), ct);

            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.Log(LogLevel.Debug,
                    $"CalExport export (simple): year={year} month={month} mode=simple includeCancelled={includeCancelled} calendars={DescribeCalendars(calendarNames)}");

            await RunProcessAsync(configPath, tempDir, ct);

            var produced = Directory.EnumerateFiles(tempDir, "*.txt").ToList();
            if (produced.Count == 0)
            {
                _logger.Log(LogLevel.Error, $"CalExport produced no TXT output file (year={year} month={month}).");
                throw new InvalidOperationException("CalExport produced no TXT output file.");
            }

            _logger.Log(LogLevel.Debug, $"CalExport produced {produced.Count} TXT file(s) for {year}-{month:D2}.");

            var destDir = Path.GetDirectoryName(Path.GetFullPath(outputFilePath));
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            File.Move(produced[0], outputFilePath, overwrite: true);
        }
        finally
        {
            TryDelete(tempDir);
        }
    }

    public async Task<IReadOnlyList<string>> ListCalendarsAsync(CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "calexport_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            _logger.Log(LogLevel.Debug, "CalExport list-calendars: enumerating local Outlook calendars.");

            await RunListProcessAsync(tempDir, ct);

            var jsonPath = Path.Combine(tempDir, "calendars.json");
            if (!File.Exists(jsonPath))
            {
                _logger.Log(LogLevel.Error, "CalExport list-calendars produced no calendars.json file.");
                throw new InvalidOperationException("CalExport produced no calendars.json file.");
            }

            var json = await File.ReadAllTextAsync(jsonPath, ct);
            var names = ParseCalendarNames(json);
            _logger.Log(LogLevel.Debug, $"CalExport list-calendars: found {names.Count} calendar(s).");
            return names;
        }
        finally
        {
            TryDelete(tempDir);
        }
    }

    // Parses the [{displayName, entryId, storeId}] array CalExport writes in --list-calendars mode
    // and projects the display names (skipping blanks). Internal so it can be unit-tested without a
    // live Outlook/process boundary.
    internal static IReadOnlyList<string> ParseCalendarNames(string json)
    {
        var arr = JArray.Parse(json);
        var names = new List<string>(arr.Count);
        foreach (var item in arr)
        {
            var name = item?["displayName"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
        }
        return names;
    }

    private async Task RunListProcessAsync(string outputDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _calExportExePath,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-l");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outputDir);

        var verbose = _logger.IsEnabled(LogLevel.Debug);
        if (verbose)
        {
            psi.ArgumentList.Add("-v");
            _logger.Log(LogLevel.Debug,
                $"Running CalExport: \"{_calExportExePath}\" {string.Join(" ", psi.ArgumentList)}");
        }

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            _logger.Log(LogLevel.Error, $"Failed to start CalExport at '{_calExportExePath}'.");
            throw new InvalidOperationException($"Failed to start CalExport at '{_calExportExePath}'.");
        }

        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await WaitForExitOrKillAsync(process, "list-calendars", ct);
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            _logger.Log(LogLevel.Error, BuildExitLogMessage(process.ExitCode, stderr));
            throw new InvalidOperationException(
                $"CalExport (list-calendars) exited with code {process.ExitCode}. {stderr}".TrimEnd());
        }
    }

    private static JObject BuildConfig(int year, int month, string mode, IReadOnlyList<string>? calendarNames, bool includeCancelled, string? outputPath)
    {
        JToken calendars = calendarNames is { Count: > 0 }
            ? new JArray(calendarNames.Cast<object>().ToArray())
            : new JValue("all");

        return new JObject
        {
            ["year"] = year,
            ["month"] = month,
            ["mode"] = mode,
            ["includeCancelled"] = includeCancelled,
            ["calendars"] = calendars,
            ["outputPath"] = outputPath == null ? JValue.CreateNull() : new JValue(outputPath),
        };
    }

    private async Task RunProcessAsync(string configPath, string outputDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _calExportExePath,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-a");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(configPath);
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outputDir);

        // Propagate verbose to the child process so it logs at Debug into the SAME per-day file.
        var verbose = _logger.IsEnabled(LogLevel.Debug);
        if (verbose)
            psi.ArgumentList.Add("-v");

        if (verbose)
            _logger.Log(LogLevel.Debug,
                $"Running CalExport: \"{_calExportExePath}\" {string.Join(" ", psi.ArgumentList)}");

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            _logger.Log(LogLevel.Error, $"Failed to start CalExport at '{_calExportExePath}'.");
            throw new InvalidOperationException($"Failed to start CalExport at '{_calExportExePath}'.");
        }

        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await WaitForExitOrKillAsync(process, "export", ct);
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            // ALWAYS log the non-zero exit with the FULL stderr — this is the key signal for
            // diagnosing why a sync did not happen. The message-only exception below truncates
            // nothing here.
            _logger.Log(LogLevel.Error, BuildExitLogMessage(process.ExitCode, stderr));
            throw new InvalidOperationException(
                $"CalExport exited with code {process.ExitCode}. {stderr}".TrimEnd());
        }
    }

    // Waits for the child to exit, but never longer than _timeout. The wait is bounded by a
    // linked CTS that fires on EITHER the caller's cancellation OR a CancelAfter(_timeout)
    // deadline. On the timeout deadline (the caller did NOT cancel) the whole child process
    // TREE is killed — Outlook may have spawned helper processes — and a CalExportTimeoutException
    // is thrown with the probable cause logged at Error. A caller-initiated cancellation is
    // re-thrown as OperationCanceledException unchanged (it is not a timeout).
    private async Task WaitForExitOrKillAsync(Process process, string operation, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(_timeout);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Timeout (not a caller cancellation): kill the whole tree so no orphan lingers, then
            // surface a distinguishable timeout failure with the probable cause.
            KillTree(process);
            var message = BuildTimeoutLogMessage(operation, _timeout);
            _logger.Log(LogLevel.Error, message);
            throw new CalExportTimeoutException(message);
        }
    }

    private void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            // Best-effort: the process may have exited in the race between the deadline and the kill.
            _logger.Log(LogLevel.Debug, $"CalExport kill-on-timeout was a no-op or failed: {ex.Message}");
        }
    }

    // The Error-level message for a CalExport timeout. Names the probable cause (an Outlook modal
    // prompt blocking the headless child) so the log alone explains a wedged sync. Internal +
    // static so it can be asserted in a unit test without standing up the real process boundary.
    internal static string BuildTimeoutLogMessage(string operation, TimeSpan timeout)
        => $"CalExport ({operation}) did not exit within {timeout.TotalMinutes:0.##} minute(s) and was killed " +
           "(process tree). Probable cause: Outlook is blocked on a modal dialog — Programmatic Access " +
           "\"Allow access\", a corrupt profile prompt, or an MFA/sign-in wall — on this device.";

    // The Error-level message for a non-zero CalExport exit. ALWAYS carries the FULL stderr so a
    // failed sync can be diagnosed from the log alone. Extracted (and internal) so it can be tested
    // without standing up the real process boundary.
    internal static string BuildExitLogMessage(int exitCode, string? stderr)
        => $"CalExport exited with code {exitCode}.{(string.IsNullOrEmpty(stderr) ? "" : " stderr: " + stderr)}".TrimEnd();

    private static string DescribeCalendars(IReadOnlyList<string>? calendarNames)
        => calendarNames is { Count: > 0 } ? string.Join(", ", calendarNames) : "all";

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch { /* best effort cleanup */ }
    }
}
