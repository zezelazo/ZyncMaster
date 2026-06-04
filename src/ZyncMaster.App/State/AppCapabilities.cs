namespace ZyncMaster.App.State;

// Device capabilities the web UI queries once at boot to gate platform-specific affordances.
// Kept as a record so it can grow (e.g. more local integrations) without a new bridge action.
public sealed record AppCapabilities
{
    // True when Outlook Classic COM automation is available on this device — gates the
    // OutlookCom source tile and the local .txt export. False on the web panel and off Windows.
    public bool OutlookCom { get; init; }
}
