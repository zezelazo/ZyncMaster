namespace ZyncMaster.CalExport;

public sealed class ParsedArguments
{
    public bool    AutoMode      { get; init; }
    public string? ConfigPath    { get; init; }
    public string? OutputPath    { get; init; }
    public bool    Verbose       { get; init; }

    // --list-calendars: headless enumeration of the local Outlook calendar folders. When set,
    // the tool connects to Outlook, emits the calendar folders as JSON (to stdout, or to a
    // "calendars.json" file inside the -o directory when one is given) and exits WITHOUT touching
    // any calendar. Independent of every other flag.
    public bool    ListCalendars { get; init; }
}
