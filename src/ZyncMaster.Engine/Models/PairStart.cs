namespace ZyncMaster.Engine;

public sealed record PairStart
{
    public string PairingId { get; init; } = "";
    public string Code { get; init; } = "";
    // PKCE verifier minted by /api/pair/start; must be presented to /api/pair/complete to claim
    // the api key (binds completion to the initiator, closing the device-code takeover).
    public string Verifier { get; init; } = "";
}
