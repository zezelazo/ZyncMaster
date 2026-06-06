using System;
using System.IO;
using System.Linq;
using ZyncMaster.Engine;

namespace ZyncMaster.App.Configuration;

// Turns the user-facing AppSettings POCO into the engine's EngineSettings, applying
// defensive defaults and clamps and validating that the required serverBaseUrl is set.
// Mirrors the Cli's CliSettingsResolver / CalImport's ImportSettingsResolver style.
public sealed class AppSettingsResolver
{
    // CalExport is bundled next to the App in a CalExport\ subfolder (by the App csproj's
    // CopyCalExport target for a VS build, and by the release workflow's publish step for the zip).
    // Resolve it from the EXE directory (AppContext.BaseDirectory), NOT the cwd, so "Sync now" /
    // the scheduler launch it regardless of where the process was started from. A user can still
    // override it via settings.json calExportPath / env, in which case this default is unused.
    private static readonly string DefaultCalExportPath =
        Path.Combine(AppContext.BaseDirectory, "CalExport", "ZyncMaster.CalExport.exe");

    // Lets a developer point the app at a local server without editing settings.json:
    // ZYNCMASTER_SERVER_URL takes precedence over the file when set.
    private const string ServerUrlEnvVar = "ZYNCMASTER_SERVER_URL";

    public EngineSettings Resolve(AppSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));

        // Precedence: ZYNCMASTER_SERVER_URL env var > serverBaseUrl in settings.json. The POCO
        // default is the production server, so a fresh install already resolves to prod here.
        var envServerUrl = Environment.GetEnvironmentVariable(ServerUrlEnvVar);
        var serverBaseUrl = !string.IsNullOrWhiteSpace(envServerUrl)
            ? envServerUrl
            : settings.ServerBaseUrl;

        if (string.IsNullOrWhiteSpace(serverBaseUrl))
            throw new SettingsValidationException(
                "'serverBaseUrl' is empty in settings.json. Set it to the ZyncMaster server URL, " +
                "for example 'https://sync.example.com'.");

        var deviceName = string.IsNullOrWhiteSpace(settings.DeviceName)
            ? Environment.MachineName
            : settings.DeviceName!;

        // The POCO defaults (14 / 10) cover the "field absent" case; here we clamp any
        // explicitly out-of-range value up to the minimum of 1.
        var syncWindowDays = Math.Max(1, settings.SyncWindowDays);
        var intervalMinutes = Math.Max(1, settings.IntervalMinutes);
        var calExportTimeoutMinutes = Math.Max(1, settings.CalExportTimeoutMinutes);

        var calExportPath = string.IsNullOrWhiteSpace(settings.CalExportPath)
            ? DefaultCalExportPath
            : settings.CalExportPath!;

        var calendars = settings.Calendars is { Length: > 0 }
            ? settings.Calendars
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .ToArray()
            : null;

        return new EngineSettings
        {
            ServerBaseUrl = serverBaseUrl!.Trim(),
            DeviceName = deviceName,
            SyncWindowDays = syncWindowDays,
            IntervalMinutes = intervalMinutes,
            CalExportPath = calExportPath,
            CalExportTimeoutMinutes = calExportTimeoutMinutes,
            CalendarNames = calendars is { Length: > 0 } ? calendars : null,
        };
    }
}
