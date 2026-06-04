namespace ZyncMaster.Server;

// Per-user store for the pool of connected calendar accounts and their encrypted refresh
// tokens. Every operation is scoped to the current authenticated user: an account owned by
// another user is never returned nor mutated (cross-user calls are no-ops / null).
public interface ICalendarAccountStore
{
    // Adds an account for the current user. refreshToken is encrypted at rest and may be
    // null for OutlookCom accounts (whose key is DeviceId).
    Task<CalendarAccount> AddAsync(CalendarAccount account, string? refreshToken, CancellationToken ct = default);

    // Gets a single account by id, scoped to the current user, or null when not found.
    Task<CalendarAccount?> GetAsync(string accountId, CancellationToken ct = default);

    // Lists every account owned by the current user.
    Task<IReadOnlyList<CalendarAccount>> ListAsync(CancellationToken ct = default);

    // Decrypts and returns the refresh token, or null when there is none or the account is
    // not owned by the current user.
    Task<string?> GetRefreshTokenAsync(string accountId, CancellationToken ct = default);

    // Atomically rotates the stored refresh token, persisting it encrypted.
    Task UpdateRefreshTokenAsync(string accountId, string refreshToken, CancellationToken ct = default);

    // Updates the lifecycle status of the account.
    Task UpdateStatusAsync(string accountId, string status, CancellationToken ct = default);

    // Changes the access scope of the account (e.g. Read -> ReadWrite).
    Task UpgradeScopeAsync(string accountId, AccountScope newScope, CancellationToken ct = default);

    // Removes the account and its encrypted refresh token. Cross-user calls are a no-op.
    Task RemoveAsync(string accountId, CancellationToken ct = default);
}
