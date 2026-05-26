namespace SyncMaster.App.Bridge;

// An inbound request from the web layer. Action selects the handler; Payload is an
// opaque JSON string the handler interprets; CorrelationId ties the reply back to the
// originating call on the web side.
public sealed record BridgeMessage
{
    public string Action { get; init; } = "";
    public string? Payload { get; init; }
    public string? CorrelationId { get; init; }
}
