using Microsoft.EntityFrameworkCore;
using ZyncMaster.Server.Data;

namespace ZyncMaster.Server;

// EF-backed device + pending-pairing store. Device queries are filtered by the current
// user. Pending-pairing rows are GLOBAL (not user-scoped): a pairing is created by an
// anonymous device (no cookie, no api key — the ambient "default" user) via
// /api/pair/start and later claimed by the real signed-in user when they approve it in
// the browser. Scoping pending lookups by user would make the approver (a different
// actor) unable to find the row, so pending codes/pairingIds are looked up globally —
// they are random and short-lived. Creates a fresh DbContext per operation through the
// factory so it can be shared by the singleton composition root and background work
// without tripping over the scoped DbContext lifetime.
public sealed class EfDeviceStore : IDeviceStore
{
    private readonly IDbContextFactory<ZyncMasterDbContext> _factory;
    private readonly ICurrentUserAccessor _currentUser;

    public EfDeviceStore(IDbContextFactory<ZyncMasterDbContext> factory, ICurrentUserAccessor currentUser)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
    }

    public async Task<Device> AddAsync(Device device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.Devices.Add(ToRow(device));
        await db.SaveChangesAsync(ct);
        return device;
    }

    public async Task<Device?> GetAsync(string deviceId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.Devices.AsNoTracking()
            .FirstOrDefaultAsync(d => d.UserId == _currentUser.UserId && d.Id == deviceId, ct);
        return row is null ? null : ToDomain(row);
    }

    public async Task<IReadOnlyList<Device>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.Devices.AsNoTracking()
            .Where(d => d.UserId == _currentUser.UserId)
            .ToListAsync(ct);
        return rows.Select(ToDomain).ToList();
    }

    public async Task UpdateAsync(Device device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.Devices
            .FirstOrDefaultAsync(d => d.UserId == _currentUser.UserId && d.Id == device.Id, ct);
        if (row is null)
            return;
        row.Name = device.Name;
        row.NameLower = NameKey(device.Name);
        row.ApiKeyHash = device.ApiKeyHash;
        row.KeyId = device.KeyId;
        row.TargetCalendarId = device.TargetCalendarId;
        row.CreatedUtc = device.CreatedUtc;
        row.LastSeenUtc = device.LastSeenUtc;
        row.Platform = device.Platform;
        row.HasOutlookCom = device.HasOutlookCom;
        row.AppVersion = device.AppVersion;
        row.LeaseUntil = device.LeaseUntil;
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(string deviceId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.Devices
            .Where(d => d.UserId == _currentUser.UserId && d.Id == deviceId)
            .ExecuteDeleteAsync(ct);
    }

    public async Task SavePendingAsync(PendingPairing pairing, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.PendingPairings.Add(ToRow(pairing));
        await db.SaveChangesAsync(ct);
    }

    public async Task<PendingPairing?> GetPendingAsync(string pairingId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pairingId);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.PendingPairings.AsNoTracking()
            .FirstOrDefaultAsync(p => p.PairingId == pairingId, ct);
        return row is null ? null : ToDomain(row);
    }

    public async Task<PendingPairing?> GetPendingByCodeAsync(
        string code, DateTimeOffset notBefore, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        await using var db = await _factory.CreateDbContextAsync(ct);
        // Match by code in the query (translatable everywhere) but enforce the TTL window in memory.
        // A DateTimeOffset relational compare (CreatedUtc >= notBefore) is NOT translatable by every
        // provider through LINQ — SQLite throws — so we evaluate it after materialising the single
        // matched row, exactly as the magic-link window and the EphemeralPurgeService do. An expired
        // row (CreatedUtc < notBefore) resolves to null so it cannot be viewed or approved.
        var row = await db.PendingPairings.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Code == code, ct);
        if (row is null || row.CreatedUtc < notBefore)
            return null;
        return ToDomain(row);
    }

    public async Task<bool> TryMarkApprovedAsync(
        string code, DateTimeOffset notBefore, string approvedDeviceId, string oneTimeApiKey,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(approvedDeviceId);
        ArgumentNullException.ThrowIfNull(oneTimeApiKey);
        await using var db = await _factory.CreateDbContextAsync(ct);

        // Atomic, idempotent claim: a single conditional UPDATE that flips Approved 0 -> 1 and
        // stamps the approved device + one-time key ONLY when the row is the matching code, still
        // unapproved, and unexpired. The Approved=0 guard makes a second approve a no-op (0 rows),
        // so it can never create a phantom device or overwrite the live OneTimeApiKey. Issued as
        // parameterised set-based SQL (injection-safe via interpolation) so the DateTimeOffset
        // compare is translated by BOTH SQL Server (prod) and SQLite (tests) — the same reason the
        // EphemeralPurgeService uses raw SQL for its DateTimeOffset predicates. Column + table names
        // mirror the ZyncMasterDbContext mapping. The bit literals 1/0 work on both providers.
        var affected = await db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE PendingPairings
               SET Approved = 1, ApprovedDeviceId = {approvedDeviceId}, OneTimeApiKey = {oneTimeApiKey}
               WHERE Code = {code} AND Approved = 0 AND CreatedUtc >= {notBefore}", ct);

        return affected >= 1;
    }

    public async Task UpdatePendingAsync(PendingPairing pairing, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.PendingPairings
            .FirstOrDefaultAsync(p => p.PairingId == pairing.PairingId, ct);
        if (row is null)
            return;
        row.DeviceName = pairing.DeviceName;
        row.Code = pairing.Code;
        row.Approved = pairing.Approved;
        row.ApprovedDeviceId = pairing.ApprovedDeviceId;
        row.OneTimeApiKey = pairing.OneTimeApiKey;
        row.CreatedUtc = pairing.CreatedUtc;
        await db.SaveChangesAsync(ct);
    }

    public async Task RemovePendingAsync(string pairingId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pairingId);
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.PendingPairings
            .Where(p => p.PairingId == pairingId)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<int> PurgeExpiredPendingAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        // Set-based delete of every pending pairing older than the TTL cutoff. Raw parameterised SQL
        // for the DateTimeOffset compare (not LINQ-translatable on SQLite — see GetPendingByCodeAsync
        // / EphemeralPurgeService). Approved-but-not-yet-completed rows are also swept: once expired,
        // the device can no longer complete the (TTL-bounded) handshake, so the row is dead weight.
        return await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM PendingPairings WHERE CreatedUtc < {cutoff}", ct);
    }

    private DeviceRow ToRow(Device d) => new()
    {
        Id = d.Id,
        // An explicit UserId on the domain device wins (used when a device is created for
        // a specific user); otherwise fall back to the ambient current user.
        UserId = string.IsNullOrEmpty(d.UserId) || d.UserId == DefaultCurrentUserAccessor.DefaultUserId
            ? _currentUser.UserId
            : d.UserId,
        Name = d.Name,
        NameLower = NameKey(d.Name),
        ApiKeyHash = d.ApiKeyHash,
        KeyId = d.KeyId,
        TargetCalendarId = d.TargetCalendarId,
        CreatedUtc = d.CreatedUtc,
        LastSeenUtc = d.LastSeenUtc,
        Platform = d.Platform,
        HasOutlookCom = d.HasOutlookCom,
        AppVersion = d.AppVersion,
        LeaseUntil = d.LeaseUntil,
    };

    // The case-insensitive uniqueness key derived from the user-typed name. Trimmed + lowercased
    // with the invariant culture so the comparison is stable across providers and cultures.
    private static string NameKey(string? name) => (name ?? string.Empty).Trim().ToLowerInvariant();

    private static Device ToDomain(DeviceRow r) => new()
    {
        Id = r.Id,
        UserId = r.UserId,
        Name = r.Name,
        ApiKeyHash = r.ApiKeyHash,
        KeyId = r.KeyId,
        TargetCalendarId = r.TargetCalendarId,
        CreatedUtc = r.CreatedUtc,
        LastSeenUtc = r.LastSeenUtc,
        Platform = r.Platform,
        HasOutlookCom = r.HasOutlookCom,
        AppVersion = r.AppVersion,
        LeaseUntil = r.LeaseUntil,
    };

    // Pending pairings are global, not user-scoped: the row is created by an anonymous
    // device and claimed by a user at approval. Leave UserId unset at save time.
    private static PendingPairingRow ToRow(PendingPairing p) => new()
    {
        PairingId = p.PairingId,
        UserId = null,
        DeviceName = p.DeviceName,
        Code = p.Code,
        Approved = p.Approved,
        ApprovedDeviceId = p.ApprovedDeviceId,
        OneTimeApiKey = p.OneTimeApiKey,
        CreatedUtc = p.CreatedUtc,
    };

    private static PendingPairing ToDomain(PendingPairingRow r) => new()
    {
        PairingId = r.PairingId,
        DeviceName = r.DeviceName,
        Code = r.Code,
        Approved = r.Approved,
        ApprovedDeviceId = r.ApprovedDeviceId,
        OneTimeApiKey = r.OneTimeApiKey,
        CreatedUtc = r.CreatedUtc,
    };
}
