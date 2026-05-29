namespace ZyncMaster.CalImport;

public sealed class ParsedImportArguments
{
    public string? SourcePath      { get; init; }
    public string? ConfigPath      { get; init; }
    public bool    AutoMode        { get; init; }
    public string? CalendarId      { get; init; }
    public string? NewCalendarName { get; init; }
    public bool    DryRun          { get; init; }
    public bool    Overwrite       { get; init; }
}
