using Newtonsoft.Json;

namespace SyncMaster.Cli;

// User-facing configuration for the sync host — settings.json next to the exe (and
// appsettings.json as a deployment-time copy). Resolved into EngineSettings through
// CliSettingsResolver, which applies defensive defaults and clamps.
public sealed class CliSettings
{
    // Required: base URL of the SyncMaster server the device pairs and syncs against.
    [JsonProperty("serverBaseUrl")]
    public string? ServerBaseUrl { get; set; }

    // Friendly name shown when approving the device; defaults to the machine name.
    [JsonProperty("deviceName")]
    public string? DeviceName { get; set; }

    // How many days ahead of "now" to sync. Defaults to 14, clamped to >= 1.
    [JsonProperty("syncWindowDays")]
    public int SyncWindowDays { get; set; } = 14;

    // Minutes between sync cycles in loop mode. Defaults to 10, clamped to >= 1.
    [JsonProperty("intervalMinutes")]
    public int IntervalMinutes { get; set; } = 10;

    // Path to SyncMaster.CalExport.exe used to read the local Outlook calendar. Defaults to
    // "SyncMaster.CalExport.exe" (resolved on PATH / next to the host).
    [JsonProperty("calExportPath")]
    public string? CalExportPath { get; set; }

    // null/empty = all calendars; otherwise the named calendars to export.
    [JsonProperty("calendars")]
    public string[]? Calendars { get; set; }
}
