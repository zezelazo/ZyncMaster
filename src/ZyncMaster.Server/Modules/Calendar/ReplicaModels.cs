namespace ZyncMaster.Server;

// Domain models for the calendar v2 replica engine (spec §7). Records (immutable); the EF rows
// live in Data/Entities.cs and the stores map between the two.

public enum ReplicaLinkStatus
{
    Active,
    Broken,    // the user deleted the replica by hand at the destination; UI offers 3 exits
    Tombstone, // closed link: write-back confirmed, discarded, removed, or origin cancelled
}

public sealed record ReplicaLink
{
    public required string Id { get; init; }
    public string UserId { get; init; } = ""; // stamped by the store from the ambient user
    public string? SourceAccountId { get; init; }
    public required string SourceEventId { get; init; }
    public string SourceGraphEventId { get; init; } = "";
    public string SourceKind { get; init; } = "graph";
    public required string DestinationAccountId { get; init; }
    public required string DestinationCalendarId { get; init; }
    public required string DestinationEventId { get; init; }
    public required string MaskTitle { get; init; }
    public string? RuleId { get; init; }
    public string ContentHash { get; init; } = "";
    public ReplicaLinkStatus Status { get; init; } = ReplicaLinkStatus.Active;
    public DateTimeOffset CreatedUtc { get; init; }
    public DateTimeOffset UpdatedUtc { get; init; }
}

public sealed record PrefixRuleDestination(string AccountId, string CalendarId);

public sealed record PrefixRule
{
    public required string Id { get; init; }
    public string UserId { get; init; } = "";
    public required string Prefix { get; init; }
    public required string MaskTitle { get; init; }
    public bool Enabled { get; init; } = true;
    public int SortOrder { get; init; }
    public IReadOnlyList<PrefixRuleDestination> Destinations { get; init; } =
        Array.Empty<PrefixRuleDestination>();
}
