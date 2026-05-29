using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ZyncMaster.Server.Data;

namespace ZyncMaster.Server;

// EF-backed connected-account store. Behaves exactly like the previous
// DataProtectionConnectedAccountStore: the refresh token is encrypted with the same
// "ZyncMaster.RefreshToken" protector and an empty / null UPN normalizes to the
// "default" key. The protected string is persisted in the EncryptedRefreshToken column,
// scoped to the current user. The domain UPN maps to the AccountRef column.
public sealed class EfConnectedAccountStore : IConnectedAccountStore
{
    private const string DefaultKey = "default";
    private const string ProviderName = "MicrosoftGraph";

    private readonly IDbContextFactory<ZyncMasterDbContext> _factory;
    private readonly ICurrentUserAccessor _currentUser;
    private readonly IDataProtector _protector;

    public EfConnectedAccountStore(
        IDbContextFactory<ZyncMasterDbContext> factory,
        ICurrentUserAccessor currentUser,
        IDataProtectionProvider dp)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        ArgumentNullException.ThrowIfNull(dp);
        _protector = dp.CreateProtector("ZyncMaster.RefreshToken");
    }

    public Task SetAsync(string userPrincipalName, string refreshToken, CancellationToken ct = default) =>
        SetForUserAsync(_currentUser.UserId, userPrincipalName, refreshToken, ct);

    public async Task SetForUserAsync(
        string userId, string userPrincipalName, string refreshToken, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentNullException.ThrowIfNull(refreshToken);
        var key = NormalizeKey(userPrincipalName);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.ConnectedAccounts
            .FirstOrDefaultAsync(a => a.UserId == userId && a.AccountRef == key, ct);
        if (row is null)
        {
            row = new ConnectedAccountRow
            {
                Id = RowId(userId, key),
                UserId = userId,
                Provider = ProviderName,
                AccountRef = key,
            };
            db.ConnectedAccounts.Add(row);
        }
        row.EncryptedRefreshToken = _protector.Protect(refreshToken);
        row.ConnectedUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task<string?> GetRefreshTokenAsync(string userPrincipalName, CancellationToken ct = default)
    {
        var row = await FindAsync(NormalizeKey(userPrincipalName), ct);
        return row is null ? null : _protector.Unprotect(row.EncryptedRefreshToken);
    }

    public async Task<ConnectedAccount?> GetAsync(string userPrincipalName, CancellationToken ct = default)
    {
        var row = await FindAsync(NormalizeKey(userPrincipalName), ct);
        return row is null ? null : ToDomain(row);
    }

    public async Task<bool> HasAnyAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.ConnectedAccounts.AnyAsync(a => a.UserId == _currentUser.UserId, ct);
    }

    public async Task<IReadOnlyList<ConnectedAccount>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.ConnectedAccounts.AsNoTracking()
            .Where(a => a.UserId == _currentUser.UserId)
            .ToListAsync(ct);
        return rows.Select(ToDomain).ToList();
    }

    public async Task RemoveAsync(string userPrincipalName, CancellationToken ct = default)
    {
        var key = NormalizeKey(userPrincipalName);
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.ConnectedAccounts
            .Where(a => a.UserId == _currentUser.UserId && a.AccountRef == key)
            .ExecuteDeleteAsync(ct);
    }

    private async Task<ConnectedAccountRow?> FindAsync(string key, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.ConnectedAccounts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.UserId == _currentUser.UserId && a.AccountRef == key, ct);
    }

    private static ConnectedAccount ToDomain(ConnectedAccountRow r) => new()
    {
        UserPrincipalName = r.AccountRef,
        EncryptedRefreshToken = r.EncryptedRefreshToken,
        ConnectedUtc = r.ConnectedUtc,
    };

    private static string RowId(string userId, string accountRef) => $"{userId}|{accountRef}";

    private static string NormalizeKey(string? userPrincipalName) =>
        string.IsNullOrWhiteSpace(userPrincipalName) ? DefaultKey : userPrincipalName;
}
