namespace SyncMaster.Server;

public sealed record TokenResult
{
    public required string AccessToken { get; init; }
    public required string RefreshToken { get; init; }
    public DateTimeOffset ExpiresUtc { get; init; }
    public string? UserPrincipalName { get; init; }
}

public sealed record ConnectedAccount
{
    public required string UserPrincipalName { get; init; }
    public required string EncryptedRefreshToken { get; init; }
    public DateTimeOffset ConnectedUtc { get; init; }
}
