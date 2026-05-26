using Newtonsoft.Json;

namespace SyncMaster.App.Configuration;

// User-facing configuration for the desktop app — settings.json next to the exe.
// Resolved into the engine's EngineSettings through AppSettingsResolver, which applies
// defensive defaults and clamps and validates the required serverBaseUrl. Mirrors
// CalImport's / the Cli's settings style: a plain POCO with [JsonProperty] names.
public sealed class AppSettings
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

    // Minutes between sync cycles. Defaults to 10, clamped to >= 1.
    [JsonProperty("intervalMinutes")]
    public int IntervalMinutes { get; set; } = 10;

    // Path to CalExport.exe used to read the local Outlook calendar. Defaults to
    // "CalExport.exe" (resolved on PATH / next to the host).
    [JsonProperty("calExportPath")]
    public string? CalExportPath { get; set; }

    // null/empty = all calendars; otherwise the named calendars to export.
    [JsonProperty("calendars")]
    public string[]? Calendars { get; set; }
}
