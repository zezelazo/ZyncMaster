namespace ZyncMaster.Server;

public interface IConnectedAccountStore
{
    Task SetAsync(string userPrincipalName, string refreshToken, CancellationToken ct = default);

    // Persists the account for an EXPLICIT user id rather than the ambient current user.
    // Used by /connect/callback: the freshly-issued auth cookie is not active within the
    // same request, so the ambient ICurrentUserAccessor would still resolve "default".
    // This path scopes the write to the just-created user directly.
    Task SetForUserAsync(string userId, string userPrincipalName, string refreshToken, CancellationToken ct = default);
    Task<string?> GetRefreshTokenAsync(string userPrincipalName, CancellationToken ct = default);
    Task<ConnectedAccount?> GetAsync(string userPrincipalName, CancellationToken ct = default);
    Task<bool> HasAnyAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ConnectedAccount>> ListAsync(CancellationToken ct = default);
    Task RemoveAsync(string userPrincipalName, CancellationToken ct = default);
}
