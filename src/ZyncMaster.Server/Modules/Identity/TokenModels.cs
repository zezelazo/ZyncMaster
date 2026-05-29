namespace ZyncMaster.Server;

public sealed record TokenResult
{
    public required string AccessToken { get; init; }
    public required string RefreshToken { get; init; }
    public DateTimeOffset ExpiresUtc { get; init; }
    public string? UserPrincipalName { get; init; }

    // Identity parsed from the id_token, used by /connect/callback to upsert the user and
    // issue the panel cookie. Subject is the stable id (oid, falling back to sub).
    public string? Subject { get; init; }
    public string? Email { get; init; }
    public string? DisplayName { get; init; }
}

public sealed record ConnectedAccount
{
    public required string UserPrincipalName { get; init; }
    public required string EncryptedRefreshToken { get; init; }
    public DateTimeOffset ConnectedUtc { get; init; }
}
