using System.Collections.Concurrent;
using Microsoft.AspNetCore.DataProtection;

namespace SyncMaster.Server;

public sealed class DataProtectionConnectedAccountStore : IConnectedAccountStore
{
    // UPN may be unknown at connect time (token service can return a null UPN);
    // fall back to this stable key so the single-user scenario still works.
    private const string DefaultKey = "default";

    private readonly IDataProtector _protector;
    private readonly ConcurrentDictionary<string, ConnectedAccount> _accounts = new(StringComparer.Ordinal);

    public DataProtectionConnectedAccountStore(IDataProtectionProvider dp)
    {
        ArgumentNullException.ThrowIfNull(dp);
        _protector = dp.CreateProtector("SyncMaster.RefreshToken");
    }

    public Task SetAsync(string userPrincipalName, string refreshToken, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);
        var key = NormalizeKey(userPrincipalName);
        var account = new ConnectedAccount
        {
            UserPrincipalName = key,
            EncryptedRefreshToken = _protector.Protect(refreshToken),
            ConnectedUtc = DateTimeOffset.UtcNow,
        };
        _accounts[key] = account;
        return Task.CompletedTask;
    }

    public Task<string?> GetRefreshTokenAsync(string userPrincipalName, CancellationToken ct = default)
    {
        var key = NormalizeKey(userPrincipalName);
        if (!_accounts.TryGetValue(key, out var account))
            return Task.FromResult<string?>(null);

        return Task.FromResult<string?>(_protector.Unprotect(account.EncryptedRefreshToken));
    }

    public Task<ConnectedAccount?> GetAsync(string userPrincipalName, CancellationToken ct = default)
    {
        var key = NormalizeKey(userPrincipalName);
        _accounts.TryGetValue(key, out var account);
        return Task.FromResult(account);
    }

    public Task<bool> HasAnyAsync(CancellationToken ct = default) =>
        Task.FromResult(!_accounts.IsEmpty);

    private static string NormalizeKey(string? userPrincipalName) =>
        string.IsNullOrWhiteSpace(userPrincipalName) ? DefaultKey : userPrincipalName;
}
