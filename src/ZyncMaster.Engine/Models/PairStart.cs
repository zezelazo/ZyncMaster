namespace ZyncMaster.Engine;

public sealed record PairStart
{
    public string PairingId { get; init; } = "";
    public string Code { get; init; } = "";
}
