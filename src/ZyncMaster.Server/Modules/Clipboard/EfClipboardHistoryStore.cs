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
//   2. FIFO item cap + age cap — keep only the newest MaxItemsPerUser rows, and drop anything older
//      than RetentionMaxAge; delete the union so the history stays a short rolling buffer on disk.
//   3. Image-byte budget — while the sum of the user's image SizeBytes exceeds
//      MaxImageTotalBytesPerUser, evict the oldest images until it fits.
public sealed class EfClipboardHistoryStore : IClipboardHistoryStore
{
    private readonly IDbContextFactory<ZyncMasterDbContext> _factory;
    private readonly ICurrentUserAccessor _user;
    private readonly IClipboardBlobStore _blobs;
    private readonly ClipboardOptions _opts;

    public EfClipboardHistoryStore(
        IDbContextFactory<ZyncMasterDbContext> factory,
        ICurrentUserAccessor user,
        IClipboardBlobStore blobs,
        IOptions<ClipboardOptions> opts)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _user = user ?? throw new ArgumentNullException(nameof(user));
        _blobs = blobs ?? throw new ArgumentNullException(nameof(blobs));
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

        // FIFO + age caps: drop everything past the newest MaxItemsPerUser rows AND anything older
        // than RetentionMaxAge. SQLite's EF provider cannot translate DateTimeOffset ordering/compares
        // to SQL; materialise then order/compare in memory — same root cause the EfDeviceStore handles
        // for its DateTimeOffset predicates. Correct on both providers; deleted by id in one round-trip.
        var byNewest = (await db.ClipboardItems
                .Where(x => x.UserId == _user.UserId)
                .Select(x => new { x.Id, x.Type, x.CreatedUtc })
                .ToListAsync(ct))
            .OrderByDescending(x => x.CreatedUtc)
            .ToList();
        var stale = new HashSet<string>(byNewest.Skip(_opts.MaxItemsPerUser).Select(x => x.Id));
        // Per-account retention: this user's ClipboardRetentionHours (if set) overrides the server
        // default. null => the global RetentionMaxAge. Resolved from the same DbContext, no extra round-trip cost.
        var userHours = await db.Users
            .Where(u => u.Id == _user.UserId)
            .Select(u => u.ClipboardRetentionHours)
            .FirstOrDefaultAsync(ct);
        var window = userHours is int h && h > 0 ? TimeSpan.FromHours(h) : _opts.RetentionMaxAge;
        if (window > TimeSpan.Zero)
        {
            var cutoff = DateTimeOffset.UtcNow - window;
            foreach (var x in byNewest)
                if (x.CreatedUtc < cutoff) stale.Add(x.Id);
        }
        if (stale.Count > 0)
        {
            await db.ClipboardItems.Where(x => stale.Contains(x.Id)).ExecuteDeleteAsync(ct);
            // An evicted File leaves its bytes on disk — delete the blob too, or retention would free
            // the metadata row but never the actual space. Best-effort (the blob store swallows misses).
            foreach (var fileId in byNewest
                         .Where(x => stale.Contains(x.Id) && x.Type == nameof(ClipboardItemType.File))
                         .Select(x => x.Id))
                await _blobs.DeleteAsync(_user.UserId, fileId, ct);
        }

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

    public async Task<ClipboardItem?> GetNewestAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        // Two-step on purpose: resolve the newest id over a slim projection (ordered client-side
        // for the DateTimeOffset/SQLite reason above), then load just that one row with its payload.
        var newestId = (await db.ClipboardItems.AsNoTracking()
                .Where(x => x.UserId == _user.UserId)
                .Select(x => new { x.Id, x.CreatedUtc })
                .ToListAsync(ct))
            .OrderByDescending(x => x.CreatedUtc)
            .Select(x => x.Id)
            .FirstOrDefault();
        if (newestId is null)
            return null;

        var row = await db.ClipboardItems.AsNoTracking()
            .FirstAsync(x => x.UserId == _user.UserId && x.Id == newestId, ct);
        return ToDomain(row);
    }

    public async Task RemoveAsync(string id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        await using var db = await _factory.CreateDbContextAsync(ct);
        // Note the type before deleting so a File's blob can be cleaned up after the row is gone.
        var type = await db.ClipboardItems.AsNoTracking()
            .Where(x => x.UserId == _user.UserId && x.Id == id)
            .Select(x => x.Type)
            .FirstOrDefaultAsync(ct);
        await db.ClipboardItems
            .Where(x => x.UserId == _user.UserId && x.Id == id)
            .ExecuteDeleteAsync(ct);
        if (type == nameof(ClipboardItemType.File))
            await _blobs.DeleteAsync(_user.UserId, id, ct);
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
