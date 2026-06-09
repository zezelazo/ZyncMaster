using Microsoft.EntityFrameworkCore;
using ZyncMaster.Server.Data;

namespace ZyncMaster.Server;

// EF-backed, user-scoped per-device clipboard settings. Mirrors EfDeviceStore /
// EfClipboardHistoryStore: the current user is resolved per call through
// ICurrentUserAccessor and a fresh DbContext is created through the factory, so the store
// is singleton-safe. Every read filters by UserId; UpsertAsync stamps the row with the
// ambient user. GetAsync returns defaults (a fresh ClipboardDeviceSettings) when no row
// exists for the device.
public sealed class EfClipboardSettingsStore : IClipboardSettingsStore
{
    private readonly IDbContextFactory<ZyncMasterDbContext> _factory;
    private readonly ICurrentUserAccessor _user;

    public EfClipboardSettingsStore(
        IDbContextFactory<ZyncMasterDbContext> factory,
        ICurrentUserAccessor user)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _user = user ?? throw new ArgumentNullException(nameof(user));
    }

    public async Task<ClipboardDeviceSettings> GetAsync(string deviceId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.ClipboardDeviceSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserId == _user.UserId && x.DeviceId == deviceId, ct);
        return row is null ? new ClipboardDeviceSettings { DeviceId = deviceId } : ToDomain(row);
    }

    public async Task<IReadOnlyList<ClipboardDeviceSettings>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.ClipboardDeviceSettings.AsNoTracking()
            .Where(x => x.UserId == _user.UserId)
            .ToListAsync(ct);
        return rows.Select(ToDomain).ToList();
    }

    public async Task UpsertAsync(ClipboardDeviceSettings s, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(s);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.ClipboardDeviceSettings
            .FirstOrDefaultAsync(x => x.UserId == _user.UserId && x.DeviceId == s.DeviceId, ct);
        if (row is null)
        {
            db.ClipboardDeviceSettings.Add(ToRow(s, _user.UserId));
        }
        else
        {
            row.AutoSync = s.AutoSync;
            row.Send = s.Send;
            row.Receive = s.Receive;
            row.ViewerHotkey = s.ViewerHotkey;
            row.Density = s.Density;
            row.ShowHints = s.ShowHints;
            row.PublicKeyBase64 = s.PublicKeyBase64;
            row.NeedsTextKey = s.NeedsTextKey;
        }
        await db.SaveChangesAsync(ct);
    }

    private static ClipboardDeviceSettingsRow ToRow(ClipboardDeviceSettings s, string userId) => new()
    {
        DeviceId = s.DeviceId,
        UserId = userId,
        AutoSync = s.AutoSync,
        Send = s.Send,
        Receive = s.Receive,
        ViewerHotkey = s.ViewerHotkey,
        Density = s.Density,
        ShowHints = s.ShowHints,
        PublicKeyBase64 = s.PublicKeyBase64,
        NeedsTextKey = s.NeedsTextKey,
    };

    private static ClipboardDeviceSettings ToDomain(ClipboardDeviceSettingsRow r) => new()
    {
        DeviceId = r.DeviceId,
        AutoSync = r.AutoSync,
        Send = r.Send,
        Receive = r.Receive,
        ViewerHotkey = r.ViewerHotkey,
        Density = r.Density,
        ShowHints = r.ShowHints,
        PublicKeyBase64 = r.PublicKeyBase64,
        NeedsTextKey = r.NeedsTextKey,
    };
}
