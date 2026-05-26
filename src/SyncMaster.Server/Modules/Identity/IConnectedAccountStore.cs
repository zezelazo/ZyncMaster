namespace SyncMaster.Server;

public interface IConnectedAccountStore
{
    Task SetAsync(string userPrincipalName, string refreshToken, CancellationToken ct = default);
    Task<string?> GetRefreshTokenAsync(string userPrincipalName, CancellationToken ct = default);
    Task<ConnectedAccount?> GetAsync(string userPrincipalName, CancellationToken ct = default);
    Task<bool> HasAnyAsync(CancellationToken ct = default);
}
