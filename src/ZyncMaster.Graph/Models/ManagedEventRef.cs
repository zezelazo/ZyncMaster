namespace ZyncMaster.Graph;

public sealed record ManagedEventRef
{
    public required string SourceId { get; init; }
    public required string EventId  { get; init; }
}
