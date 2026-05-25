namespace SyncMaster.CalExport;

public sealed class ParsedArguments
{
    public bool    AutoMode   { get; init; }
    public string? ConfigPath { get; init; }
    public string? OutputPath { get; init; }
}
