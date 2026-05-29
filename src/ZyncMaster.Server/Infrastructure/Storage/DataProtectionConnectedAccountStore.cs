using System.Collections.Concurrent;
using Microsoft.AspNetCore.DataProtection;

namespace ZyncMaster.Server;

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
        _protector = dp.CreateProtector("ZyncMaster.RefreshToken");
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

    // This in-memory double is not user-partitioned; the explicit user id is irrelevant to
    // its single keyspace, so it simply stores under the UPN like SetAsync.
    public Task SetForUserAsync(
        string userId, string userPrincipalName, string refreshToken, CancellationToken ct = default) =>
        SetAsync(userPrincipalName, refreshToken, ct);

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

    public Task<IReadOnlyList<ConnectedAccount>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ConnectedAccount>>(_accounts.Values.ToList());

    public Task RemoveAsync(string userPrincipalName, CancellationToken ct = default)
    {
        var key = NormalizeKey(userPrincipalName);
        _accounts.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    private static string NormalizeKey(string? userPrincipalName) =>
        string.IsNullOrWhiteSpace(userPrincipalName) ? DefaultKey : userPrincipalName;
}
