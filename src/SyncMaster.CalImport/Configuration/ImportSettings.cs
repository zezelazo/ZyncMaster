using Newtonsoft.Json;

namespace SyncMaster.CalImport;

// User preferences — settings.json, user-friendly. Generated next to the exe and
// updated by the interactive "Save as defaults" prompt. Technical/deployment values
// (clientId, authority, extendedPropertyGuid) live in AppConfig / appsettings.json.
public sealed class ImportSettings
{
    // Optional sign-in hint to pre-fill the account in the browser prompt.
    [JsonProperty("accountHint")]
    public string? AccountHint { get; set; }

    // null = ask which calendar each run; a calendar id = import there by default.
    [JsonProperty("defaultCalendarId")]
    public string? DefaultCalendarId { get; set; }

    // One reminder per event — Outlook supports only one.
    [JsonProperty("reminderMinutes")]
    public int ReminderMinutes { get; set; } = 30;
}
