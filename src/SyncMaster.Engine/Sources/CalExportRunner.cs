using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SyncMaster.Engine;

// Drives the headless CalExport.exe to produce a single month's Complete-mode JSON.
//
// This is an untested process boundary (like OutlookCalendarService): it shells out
// to an external executable and reads its file output. It is consumed through
// ICalExportRunner so OutlookComSource can be unit-tested with a fake runner.
public sealed class CalExportRunner : ICalExportRunner
{
    private readonly string _calExportExePath;

    public CalExportRunner(string calExportExePath)
    {
        _calExportExePath = calExportExePath ?? throw new ArgumentNullException(nameof(calExportExePath));
    }

    public async Task<string> ExportMonthAsync(int year, int month, IReadOnlyList<string>? calendarNames, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "calexport_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.json");

        try
        {
            JToken calendars = calendarNames is { Count: > 0 }
                ? new JArray(calendarNames.Cast<object>().ToArray())
                : new JValue("all");

            var config = new JObject
            {
                ["year"] = year,
                ["month"] = month,
                ["mode"] = "complete",
                ["includeCancelled"] = true,
                ["calendars"] = calendars,
                ["outputPath"] = JValue.CreateNull(),
            };
            await File.WriteAllTextAsync(configPath, config.ToString(Formatting.Indented), ct);

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
            psi.ArgumentList.Add(tempDir);

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
                throw new InvalidOperationException($"Failed to start CalExport at '{_calExportExePath}'.");

            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"CalExport exited with code {process.ExitCode}. {stderr}".TrimEnd());

            var produced = Directory
                .EnumerateFiles(tempDir, "*.json")
                .Where(f => !string.Equals(f, configPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (produced.Count == 0)
                throw new InvalidOperationException("CalExport produced no JSON output file.");

            return await File.ReadAllTextAsync(produced[0], ct);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch { /* best effort cleanup */ }
        }
    }
}
