using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ZyncMaster.Server.Data;

namespace ZyncMaster.Server;

// EF-backed calendar account pool store. Refresh tokens are encrypted at rest with a
// dedicated "ZyncMaster.CalendarToken" protector purpose (distinct from the identity
// "ZyncMaster.RefreshToken" / "IdentityToken" purposes) and stored inline on the account
// row, so rotation is a single atomic row update and removal a single delete. Every
// operation is scoped to ICurrentUserAccessor.UserId; cross-user reads return null and
// cross-user mutations are no-ops.
public sealed class EfCalendarAccountStore : ICalendarAccountStore
{
    private readonly IDbContextFactory<ZyncMasterDbContext> _factory;
    private readonly ICurrentUserAccessor _currentUser;
    private readonly IDataProtector _protector;

    public EfCalendarAccountStore(
        IDbContextFactory<ZyncMasterDbContext> factory,
        ICurrentUserAccessor currentUser,
        IDataProtectionProvider dp)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        ArgumentNullException.ThrowIfNull(dp);
        _protector = dp.CreateProtector("ZyncMaster.CalendarToken");
    }

    public async Task<CalendarAccount> AddAsync(
        CalendarAccount account, string? refreshToken, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        var userId = _currentUser.UserId;

        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = new CalendarAccountRow
        {
            Id = account.Id,
            UserId = userId,
            Kind = account.Kind.ToString(),
            Provider = account.Provider,
            AccountEmail = account.AccountEmail,
            Authority = account.Authority,
            Scope = account.Scope.ToString(),
            DeviceId = account.DeviceId,
            DisplayName = account.DisplayName,
            EncryptedRefreshToken = string.IsNullOrEmpty(refreshToken) ? null : _protector.Protect(refreshToken),
            Status = account.Status,
            ConnectedAt = account.ConnectedAt,
        };
        db.CalendarAccounts.Add(row);
        await db.SaveChangesAsync(ct);

        return ToDomain(row);
    }

    public async Task<CalendarAccount?> GetAsync(string accountId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountId);
        var row = await FindAsync(accountId, track: false, ct);
        return row is null ? null : ToDomain(row);
    }

    public async Task<IReadOnlyList<CalendarAccount>> ListAsync(CancellationToken ct = default)
    {
        var userId = _currentUser.UserId;
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.CalendarAccounts.AsNoTracking()
            .Where(a => a.UserId == userId)
            .ToListAsync(ct);
        return rows.Select(ToDomain).ToList();
    }

    public async Task<string?> GetRefreshTokenAsync(string accountId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountId);
        var row = await FindAsync(accountId, track: false, ct);
        return row?.EncryptedRefreshToken is null ? null : _protector.Unprotect(row.EncryptedRefreshToken);
    }

    public async Task UpdateRefreshTokenAsync(
        string accountId, string refreshToken, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountId);
        ArgumentException.ThrowIfNullOrEmpty(refreshToken);

        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await TrackedAsync(db, accountId, ct);
        if (row is null) return;

        row.EncryptedRefreshToken = _protector.Protect(refreshToken);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateStatusAsync(string accountId, string status, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountId);
        ArgumentException.ThrowIfNullOrEmpty(status);

        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await TrackedAsync(db, accountId, ct);
        if (row is null) return;

        row.Status = status;
        await db.SaveChangesAsync(ct);
    }

    public async Task UpgradeScopeAsync(string accountId, AccountScope newScope, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountId);

        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await TrackedAsync(db, accountId, ct);
        if (row is null) return;

        row.Scope = newScope.ToString();
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(string accountId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountId);
        var userId = _currentUser.UserId;

        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.CalendarAccounts
            .Where(a => a.Id == accountId && a.UserId == userId)
            .ExecuteDeleteAsync(ct);
    }

    private async Task<CalendarAccountRow?> FindAsync(string accountId, bool track, CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        await using var db = await _factory.CreateDbContextAsync(ct);
        var query = db.CalendarAccounts.AsQueryable();
        if (!track) query = query.AsNoTracking();
        return await query.FirstOrDefaultAsync(a => a.Id == accountId && a.UserId == userId, ct);
    }

    private async Task<CalendarAccountRow?> TrackedAsync(
        ZyncMasterDbContext db, string accountId, CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        return await db.CalendarAccounts
            .FirstOrDefaultAsync(a => a.Id == accountId && a.UserId == userId, ct);
    }

    private static CalendarAccount ToDomain(CalendarAccountRow r) => new()
    {
        Id = r.Id,
        UserId = r.UserId,
        Kind = Enum.Parse<AccountKind>(r.Kind),
        Provider = r.Provider,
        AccountEmail = r.AccountEmail,
        Authority = r.Authority,
        Scope = Enum.Parse<AccountScope>(r.Scope),
        DeviceId = r.DeviceId,
        DisplayName = r.DisplayName,
        ConnectedAt = r.ConnectedAt,
        Status = r.Status,
    };
}
