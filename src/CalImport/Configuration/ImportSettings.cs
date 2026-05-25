using Newtonsoft.Json;

namespace SyncMaster.CalImport;

public sealed class ImportSettings
{
    // DO NOT CHANGE THIS GUID. Changing it makes every event previously
    // imported by CalImport invisible to subsequent runs (the new GUID won't
    // match the singleValueExtendedProperty already attached to those events),
    // and re-imports will create silent duplicates in the destination calendar.
    // If you genuinely need a new namespace, document a manual migration plan.
    public const string DefaultExtendedPropertyGuid = "1c5e1a3f-6d7b-4f1a-9c2e-3a4b5c6d7e8f";


    // Azure AD app registration client id (public client, no secret).
    [JsonProperty("clientId")]
    public string ClientId { get; set; } = "";

    // Use "consumers" for personal Microsoft accounts (outlook.com / hotmail.com / live.com / msn.com).
    // Use "organizations" or a tenant GUID for work/school accounts.
    [JsonProperty("authority")]
    public string Authority { get; set; } = "https://login.microsoftonline.com/consumers";

    // Optional hint shown to MSAL during sign-in to pre-fill the account.
    [JsonProperty("accountHint")]
    public string? AccountHint { get; set; }

    // null  = always prompt for a calendar in interactive mode (or fail in --auto).
    // "id"  = use this calendar id by default.
    [JsonProperty("defaultCalendarId")]
    public string? DefaultCalendarId { get; set; }

    // Reminder configured on every imported event. Outlook supports only one reminder per event.
    [JsonProperty("reminderMinutes")]
    public int ReminderMinutes { get; set; } = 30;

    // Namespace for the singleValueExtendedProperty that stores the source id.
    // Fixed for the project — never change, or existing events become unfindable.
    [JsonProperty("extendedPropertyGuid")]
    public string ExtendedPropertyGuid { get; set; } = DefaultExtendedPropertyGuid;
}
