using Newtonsoft.Json;

namespace ZyncMaster.App.Configuration;

// User-facing configuration for the desktop app — settings.json next to the exe.
// Resolved into the engine's EngineSettings through AppSettingsResolver, which applies
// defensive defaults and clamps and validates the required serverBaseUrl. Mirrors
// CalImport's / the Cli's settings style: a plain POCO with [JsonProperty] names.
public sealed class AppSettings
{
    // Production server the released app pairs and syncs against. Used as the default for
    // serverBaseUrl so a fresh install works out of the box without hand-editing settings.json.
    // A developer can point at a local server by setting the ZYNCMASTER_SERVER_URL environment
    // variable (honoured in AppSettingsResolver) or by editing serverBaseUrl in settings.json.
    public const string ProductionServerBaseUrl = "https://api.devlabperu.com/zync";

    // Base URL of the ZyncMaster server the device pairs and syncs against. Defaults to the
    // production server; the generated settings.json therefore ships pointing at prod, and the
    // user can override it there (or via ZYNCMASTER_SERVER_URL) to target another environment.
    [JsonProperty("serverBaseUrl")]
    public string? ServerBaseUrl { get; set; } = ProductionServerBaseUrl;

    // Friendly name shown when approving the device; defaults to the machine name.
    [JsonProperty("deviceName")]
    public string? DeviceName { get; set; }

    // How many days ahead of "now" to sync. Defaults to 14, clamped to >= 1.
    [JsonProperty("syncWindowDays")]
    public int SyncWindowDays { get; set; } = 14;

    // Minutes between sync cycles. Defaults to 10, clamped to >= 1.
    [JsonProperty("intervalMinutes")]
    public int IntervalMinutes { get; set; } = 10;

    // Path to ZyncMaster.CalExport.exe used to read the local Outlook calendar. Defaults to
    // "ZyncMaster.CalExport.exe" (resolved on PATH / next to the host).
    [JsonProperty("calExportPath")]
    public string? CalExportPath { get; set; }

    // Hard cap (minutes) on a single headless CalExport child process before it is killed. Guards
    // against Outlook blocking the child on a modal dialog (Programmatic Access, corrupt profile,
    // MFA) which would otherwise wedge the scheduler. Defaults to 5, clamped to >= 1.
    [JsonProperty("calExportTimeoutMinutes")]
    public int CalExportTimeoutMinutes { get; set; } = 5;

    // null/empty = all calendars; otherwise the named calendars to export.
    [JsonProperty("calendars")]
    public string[]? Calendars { get; set; }
}
