namespace ZyncMaster.App.Bridge;

// The response to a BridgeMessage, sent back over the transport. CorrelationId echoes
// the request; Ok signals success; Payload carries the serialized result on success;
// Error carries the message on failure. Exactly one of Payload/Error is meaningful.
public sealed record BridgeReply
{
    public string CorrelationId { get; init; } = "";
    public bool Ok { get; init; }
    public string? Payload { get; init; }
    public string? Error { get; init; }
}
