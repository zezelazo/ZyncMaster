using Newtonsoft.Json;

namespace SyncMaster.CalImport;

// Deployment configuration — NOT user preferences. Lives in appsettings.json,
// copied next to the exe on every build (stable across clean/rebuild). The user
// sets clientId once after registering the Azure app; these values are technical
// and are deliberately kept out of the user-friendly settings.json.
public sealed class AppConfig
{
    // Fixed project namespace for the singleValueExtendedProperty that stores the
    // source id on each Graph event. DO NOT CHANGE — changing it makes every event
    // previously imported by CalImport invisible on the next run and causes silent
    // duplicates in the destination calendar.
    public const string DefaultExtendedPropertyGuid = "1c5e1a3f-6d7b-4f1a-9c2e-3a4b5c6d7e8f";

    // Azure AD app registration "Application (client) ID" (public client, no secret).
    [JsonProperty("clientId")]
    public string ClientId { get; set; } = "";

    // "consumers" for personal Microsoft accounts (outlook.com / hotmail.com / live.com / msn.com).
    // "organizations" or a tenant GUID for work/school accounts.
    [JsonProperty("authority")]
    public string Authority { get; set; } = "https://login.microsoftonline.com/consumers";

    [JsonProperty("extendedPropertyGuid")]
    public string ExtendedPropertyGuid { get; set; } = DefaultExtendedPropertyGuid;
}
