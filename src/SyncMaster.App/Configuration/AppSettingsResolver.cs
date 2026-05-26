using System;
using System.Linq;
using SyncMaster.Engine;

namespace SyncMaster.App.Configuration;

// Turns the user-facing AppSettings POCO into the engine's EngineSettings, applying
// defensive defaults and clamps and validating that the required serverBaseUrl is set.
// Mirrors the Cli's CliSettingsResolver / CalImport's ImportSettingsResolver style.
public sealed class AppSettingsResolver
{
    private const string DefaultCalExportPath = "CalExport.exe";

    public EngineSettings Resolve(AppSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));

        if (string.IsNullOrWhiteSpace(settings.ServerBaseUrl))
            throw new SettingsValidationException(
                "'serverBaseUrl' is empty in settings.json. Set it to the SyncMaster server URL, " +
                "for example 'https://sync.example.com'.");

        var deviceName = string.IsNullOrWhiteSpace(settings.DeviceName)
            ? Environment.MachineName
            : settings.DeviceName!;

        // The POCO defaults (14 / 10) cover the "field absent" case; here we clamp any
        // explicitly out-of-range value up to the minimum of 1.
        var syncWindowDays = Math.Max(1, settings.SyncWindowDays);
        var intervalMinutes = Math.Max(1, settings.IntervalMinutes);

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
            ServerBaseUrl = settings.ServerBaseUrl!.Trim(),
            DeviceName = deviceName,
            SyncWindowDays = syncWindowDays,
            IntervalMinutes = intervalMinutes,
            CalExportPath = calExportPath,
            CalendarNames = calendars is { Length: > 0 } ? calendars : null,
        };
    }
}
