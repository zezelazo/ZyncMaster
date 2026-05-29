namespace ZyncMaster.Engine;

// One side of a sync pair. Provider is "OutlookCom" (read locally via Outlook COM)
// or "MicrosoftGraph" (read/write through the server's Graph connection).
public sealed record Endpoint
{
    public string Provider { get; init; } = "";
    public string? AccountRef { get; init; }
    public string CalendarId { get; init; } = "";
    public string CalendarName { get; init; } = "";
}
