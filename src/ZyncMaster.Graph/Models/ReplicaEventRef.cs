namespace ZyncMaster.Graph;

// A replica event found in a destination-calendar window scan: the Graph event id plus the
// opaque source id carried by its ZmReplicaOf property. Used to detect manually-deleted
// replicas with ONE paginated read per destination calendar instead of N GETs.
public sealed class ReplicaEventRef
{
    public string EventId       { get; init; } = "";
    public string SourceEventId { get; init; } = "";
}
