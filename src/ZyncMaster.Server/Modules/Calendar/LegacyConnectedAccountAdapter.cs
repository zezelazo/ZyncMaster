using ZyncMaster.Core;

namespace ZyncMaster.Server;

// Bridges the legacy per-UPN connected-account store into the new accountId-keyed pool world
// (plan v2 §C-3). See ILegacyConnectedAccountAdapter for the contract and the retirement
// criterion. Resolution is lazy on read: nothing is migrated or written here.
public sealed class LegacyConnectedAccountAdapter : ILegacyConnectedAccountAdapter
{
    // FIXED project namespace for deriving a stable accountId from a legacy (userId, accountRef)
    // pair. NEVER change this GUID: every legacy account's id, and every pair that references it,
    // is anchored to it. Generated once for Track A-3; documented as a constant on purpose.
    public static readonly Guid AdapterNamespace = new("7c3f1d2a-9b84-5e16-a0d7-2f4c6b8e1a93");

    // The legacy store normalizes an empty/blank UPN to this key; we mirror it before hashing so
    // the derived id matches whatever the legacy store will actually look up.
    private const string DefaultKey = "default";

    private readonly ICalendarAccountStore _pool;
    private readonly IConnectedAccountStore _legacy;
    private readonly ICurrentUserAccessor _currentUser;

    public LegacyConnectedAccountAdapter(
        ICalendarAccountStore pool,
        IConnectedAccountStore legacy,
        ICurrentUserAccessor currentUser)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _legacy = legacy ?? throw new ArgumentNullException(nameof(legacy));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
    }

    public string DeriveAccountId(string userId, string? accountRef)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);
        var key = NormalizeRef(accountRef);
        return UuidV5.Create(AdapterNamespace, $"{userId}|{key}").ToString("N");
    }

    public async Task<CalendarAccount?> ResolveAsync(string accountId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountId);

        // New pool wins: a real pool account is authoritative and carries its own metadata.
        var pooled = await _pool.GetAsync(accountId, ct).ConfigureAwait(false);
        if (pooled is not null)
            return pooled;

        // Fall back to the legacy store: find the (one) legacy account whose derived id matches.
        var legacy = await FindLegacyByIdAsync(accountId, ct).ConfigureAwait(false);
        return legacy is null ? null : WrapLegacy(accountId, legacy);
    }

    public async Task<string?> ResolveRefreshTokenAsync(string accountId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountId);

        // New pool first.
        var pooled = await _pool.GetRefreshTokenAsync(accountId, ct).ConfigureAwait(false);
        if (pooled is not null)
            return pooled;

        // Legacy: locate the account by derived id, then decrypt its token via its UPN key.
        var legacy = await FindLegacyByIdAsync(accountId, ct).ConfigureAwait(false);
        if (legacy is null)
            return null;
        return await _legacy.GetRefreshTokenAsync(legacy.UserPrincipalName, ct).ConfigureAwait(false);
    }

    public async Task UpdateRefreshTokenAsync(string accountId, string refreshToken, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountId);
        ArgumentException.ThrowIfNullOrEmpty(refreshToken);

        // If the id belongs to a real pool account, rotate it there.
        if (await _pool.GetAsync(accountId, ct).ConfigureAwait(false) is not null)
        {
            await _pool.UpdateRefreshTokenAsync(accountId, refreshToken, ct).ConfigureAwait(false);
            return;
        }

        // Otherwise rotate the legacy account this id derives from (keyed by its UPN).
        var legacy = await FindLegacyByIdAsync(accountId, ct).ConfigureAwait(false);
        if (legacy is not null)
            await _legacy.SetAsync(legacy.UserPrincipalName, refreshToken, ct).ConfigureAwait(false);
    }

    public async Task<string> ResolveAccountIdAsync(string? accountRef, CancellationToken ct = default)
    {
        // A non-empty ref that is already a real pool accountId is canonical as-is.
        if (!string.IsNullOrWhiteSpace(accountRef) &&
            await _pool.GetAsync(accountRef, ct).ConfigureAwait(false) is not null)
        {
            return accountRef;
        }

        // The ref may ALREADY be a canonical derived accountId (e.g. a pair stored under the
        // adapter id). If it matches a legacy account's derived id, it is canonical as-is — do
        // NOT re-derive (hashing a hash) which would produce an unresolvable id.
        if (!string.IsNullOrWhiteSpace(accountRef) &&
            await FindLegacyByIdAsync(accountRef, ct).ConfigureAwait(false) is not null)
        {
            return accountRef;
        }

        // Otherwise treat the ref as a legacy UPN ("" / "default" included) and derive its id.
        return DeriveAccountId(_currentUser.UserId, accountRef);
    }

    // Scans the current user's legacy accounts and returns the one whose deterministic id equals
    // accountId. The legacy store is already user-scoped, so this only ever sees the caller's
    // accounts. There is at most a handful of legacy accounts per user (typically one), so the
    // linear scan is cheap and avoids persisting any mapping.
    private async Task<ConnectedAccount?> FindLegacyByIdAsync(string accountId, CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        var all = await _legacy.ListAsync(ct).ConfigureAwait(false);
        foreach (var acc in all)
        {
            if (string.Equals(DeriveAccountId(userId, acc.UserPrincipalName), accountId, StringComparison.Ordinal))
                return acc;
        }
        return null;
    }

    // Projects a legacy ConnectedAccount into a CalendarAccount under the derived accountId. The
    // legacy store only knows the UPN + token + connected-time, so the rest is filled with the
    // Graph defaults the legacy single-account flow always implied (Microsoft / read-write).
    private CalendarAccount WrapLegacy(string accountId, ConnectedAccount legacy) => new()
    {
        Id = accountId,
        UserId = _currentUser.UserId,
        Kind = AccountKind.Graph,
        Provider = "microsoft",
        AccountEmail = string.Equals(legacy.UserPrincipalName, DefaultKey, StringComparison.Ordinal)
            ? ""
            : legacy.UserPrincipalName,
        Scope = AccountScope.ReadWrite,
        DisplayName = string.Equals(legacy.UserPrincipalName, DefaultKey, StringComparison.Ordinal)
            ? "Connected account"
            : legacy.UserPrincipalName,
        Status = "active",
        ConnectedAt = legacy.ConnectedUtc,
    };

    private static string NormalizeRef(string? accountRef) =>
        string.IsNullOrWhiteSpace(accountRef) ? DefaultKey : accountRef;
}
