using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ZyncMaster.Engine;

// Drives the headless ZyncMaster.CalExport.exe to produce a single month's Complete-mode JSON.
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
            var config = BuildConfig(year, month, "complete", calendarNames, outputPath: null);
            await File.WriteAllTextAsync(configPath, config.ToString(Formatting.Indented), ct);

            await RunProcessAsync(configPath, tempDir, ct);

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
            TryDelete(tempDir);
        }
    }

    public async Task ExportSimpleAsync(int year, int month, IReadOnlyList<string>? calendarNames, string outputFilePath, CancellationToken ct)
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
            var config = BuildConfig(year, month, "simple", calendarNames, outputPath: null);
            await File.WriteAllTextAsync(configPath, config.ToString(Formatting.Indented), ct);

            await RunProcessAsync(configPath, tempDir, ct);

            var produced = Directory.EnumerateFiles(tempDir, "*.txt").ToList();
            if (produced.Count == 0)
                throw new InvalidOperationException("CalExport produced no TXT output file.");

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

    private static JObject BuildConfig(int year, int month, string mode, IReadOnlyList<string>? calendarNames, string? outputPath)
    {
        JToken calendars = calendarNames is { Count: > 0 }
            ? new JArray(calendarNames.Cast<object>().ToArray())
            : new JValue("all");

        return new JObject
        {
            ["year"] = year,
            ["month"] = month,
            ["mode"] = mode,
            ["includeCancelled"] = true,
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

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
            throw new InvalidOperationException($"Failed to start CalExport at '{_calExportExePath}'.");

        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"CalExport exited with code {process.ExitCode}. {stderr}".TrimEnd());
    }

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
