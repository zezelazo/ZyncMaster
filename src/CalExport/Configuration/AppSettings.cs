using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SyncMaster.CalExport;

public class AppSettings
{
    // "current", "previous", or a number like 2025
    [JsonProperty("year")]
    public JToken Year { get; set; } = new JValue("current");

    // "current", "previous" or 1-12
    [JsonProperty("month")]
    public JToken Month { get; set; } = new JValue("current");

    // "simple" or "complete"
    [JsonProperty("mode")]
    public string Mode { get; set; } = "complete";

    [JsonProperty("includeCancelled")]
    public bool IncludeCancelled { get; set; } = true;

    // "all" or array of calendar display names e.g. ["Calendar [user@company.com]"]
    [JsonProperty("calendars")]
    public JToken Calendars { get; set; } = new JValue("all");

    // null or "" means same directory as the executable
    [JsonProperty("outputPath")]
    public string? OutputPath { get; set; } = null;
}
