using Microsoft.EntityFrameworkCore;
using ZyncMaster.Server.Data;

namespace ZyncMaster.Server;

// EF-backed device + pending-pairing store. Every query is filtered by the current
// user (the "default" stub for now). Creates a fresh DbContext per operation through
// the factory so it can be shared by the singleton composition root and background work
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
        row.ApiKeyHash = device.ApiKeyHash;
        row.TargetCalendarId = device.TargetCalendarId;
        row.CreatedUtc = device.CreatedUtc;
        row.LastSeenUtc = device.LastSeenUtc;
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
            .FirstOrDefaultAsync(p => p.UserId == _currentUser.UserId && p.PairingId == pairingId, ct);
        return row is null ? null : ToDomain(row);
    }

    public async Task<PendingPairing?> GetPendingByCodeAsync(string code, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.PendingPairings.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == _currentUser.UserId && p.Code == code, ct);
        return row is null ? null : ToDomain(row);
    }

    public async Task UpdatePendingAsync(PendingPairing pairing, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.PendingPairings
            .FirstOrDefaultAsync(p => p.UserId == _currentUser.UserId && p.PairingId == pairing.PairingId, ct);
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
            .Where(p => p.UserId == _currentUser.UserId && p.PairingId == pairingId)
            .ExecuteDeleteAsync(ct);
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
        ApiKeyHash = d.ApiKeyHash,
        TargetCalendarId = d.TargetCalendarId,
        CreatedUtc = d.CreatedUtc,
        LastSeenUtc = d.LastSeenUtc,
    };

    private static Device ToDomain(DeviceRow r) => new()
    {
        Id = r.Id,
        UserId = r.UserId,
        Name = r.Name,
        ApiKeyHash = r.ApiKeyHash,
        TargetCalendarId = r.TargetCalendarId,
        CreatedUtc = r.CreatedUtc,
        LastSeenUtc = r.LastSeenUtc,
    };

    private PendingPairingRow ToRow(PendingPairing p) => new()
    {
        PairingId = p.PairingId,
        UserId = _currentUser.UserId,
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
