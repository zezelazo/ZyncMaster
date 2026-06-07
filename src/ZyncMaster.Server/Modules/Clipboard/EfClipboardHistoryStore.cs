using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZyncMaster.Server.Data;

namespace ZyncMaster.Server;

// EF-backed, user-scoped clipboard history. Every query is filtered by the current user
// (resolved per call through ICurrentUserAccessor — singleton-safe, same pattern as
// EfDeviceStore). A fresh DbContext is created per operation through the factory so the
// store can be registered as a singleton over IDbContextFactory.
//
// Eviction policy applied on every Append, in order:
//   1. Hard image ceiling — an image over HardMaxImageBytes is rejected (throws), never stored.
//   2. FIFO item cap — keep only the newest MaxItemsPerUser rows; delete the overflow.
//   3. Image-byte budget — while the sum of the user's image SizeBytes exceeds
//      MaxImageTotalBytesPerUser, evict the oldest images until it fits.
public sealed class EfClipboardHistoryStore : IClipboardHistoryStore
{
    private readonly IDbContextFactory<ZyncMasterDbContext> _factory;
    private readonly ICurrentUserAccessor _user;
    private readonly ClipboardOptions _opts;

    public EfClipboardHistoryStore(
        IDbContextFactory<ZyncMasterDbContext> factory,
        ICurrentUserAccessor user,
        IOptions<ClipboardOptions> opts)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _opts = (opts ?? throw new ArgumentNullException(nameof(opts))).Value;
    }

    public async Task AppendAsync(ClipboardItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.Type == ClipboardItemType.Image && (item.SizeBytes ?? 0) > _opts.HardMaxImageBytes)
            throw new ClipboardImageTooLargeException(item.SizeBytes ?? 0, _opts.HardMaxImageBytes);

        await using var db = await _factory.CreateDbContextAsync(ct);

        // Stamp the row with the ambient user, ignoring whatever UserId the caller put on
        // the domain item — the store is the authority on ownership.
        db.ClipboardItems.Add(ToRow(item with { UserId = _user.UserId }));
        await db.SaveChangesAsync(ct);

        // FIFO cap: drop everything past the newest MaxItemsPerUser rows. SQLite can't
        // ORDER BY a DateTimeOffset in SQL (same limitation EfDeviceStore documents), so
        // the rows are materialised and ordered client-side — correct on both providers.
        var byNewest = (await db.ClipboardItems
                .Where(x => x.UserId == _user.UserId)
                .Select(x => new { x.Id, x.CreatedUtc })
                .ToListAsync(ct))
            .OrderByDescending(x => x.CreatedUtc)
            .ToList();
        var overflow = byNewest.Skip(_opts.MaxItemsPerUser).Select(x => x.Id).ToList();
        if (overflow.Count > 0)
            await db.ClipboardItems.Where(x => overflow.Contains(x.Id)).ExecuteDeleteAsync(ct);

        // Image-byte budget: evict oldest images until the running total fits. Ordered
        // client-side for the same DateTimeOffset/SQLite reason as above.
        var images = (await db.ClipboardItems
                .Where(x => x.UserId == _user.UserId && x.Type == nameof(ClipboardItemType.Image))
                .Select(x => new { x.Id, x.SizeBytes, x.CreatedUtc })
                .ToListAsync(ct))
            .OrderBy(x => x.CreatedUtc)
            .ToList();
        var total = images.Sum(i => i.SizeBytes ?? 0);
        var toEvict = new List<string>();
        foreach (var img in images)
        {
            if (total <= _opts.MaxImageTotalBytesPerUser)
                break;
            toEvict.Add(img.Id);
            total -= img.SizeBytes ?? 0;
        }
        if (toEvict.Count > 0)
            await db.ClipboardItems.Where(x => toEvict.Contains(x.Id)).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<ClipboardItem>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        // Client-side order/take: SQLite can't ORDER BY DateTimeOffset in SQL. The set is
        // bounded by the FIFO cap enforced on Append, so materialising it is cheap.
        var rows = (await db.ClipboardItems.AsNoTracking()
                .Where(x => x.UserId == _user.UserId)
                .ToListAsync(ct))
            .OrderByDescending(x => x.CreatedUtc)
            .Take(_opts.MaxItemsPerUser)
            .ToList();
        return rows.Select(ToDomain).ToList();
    }

    public async Task RemoveAsync(string id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.ClipboardItems
            .Where(x => x.UserId == _user.UserId && x.Id == id)
            .ExecuteDeleteAsync(ct);
    }

    private static ClipboardItemRow ToRow(ClipboardItem i) => new()
    {
        Id = i.Id,
        UserId = i.UserId,
        Type = i.Type.ToString(),
        OriginDeviceId = i.OriginDeviceId,
        OriginDeviceName = i.OriginDeviceName,
        CreatedUtc = i.CreatedUtc,
        SizeBytes = i.SizeBytes,
        Payload = i.Payload,
        Thumbnail = i.Thumbnail,
        Preview = i.Preview,
    };

    private static ClipboardItem ToDomain(ClipboardItemRow r) => new()
    {
        Id = r.Id,
        UserId = r.UserId,
        Type = Enum.Parse<ClipboardItemType>(r.Type),
        OriginDeviceId = r.OriginDeviceId,
        OriginDeviceName = r.OriginDeviceName,
        CreatedUtc = r.CreatedUtc,
        SizeBytes = r.SizeBytes,
        Payload = r.Payload,
        Thumbnail = r.Thumbnail,
        Preview = r.Preview,
    };
}
