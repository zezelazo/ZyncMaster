using Microsoft.EntityFrameworkCore;
using ZyncMaster.Server.Data;

namespace ZyncMaster.Server;

// EF-backed replica link store. Every operation is scoped to ICurrentUserAccessor.UserId:
// AddAsync STAMPS the ambient user (callers never choose the owner), reads filter by it, and
// UpdateAsync matches on (Id, UserId) so a cross-user mutation is a no-op returning false.
public sealed class EfReplicaLinkStore : IReplicaLinkStore
{
    private readonly IDbContextFactory<ZyncMasterDbContext> _factory;
    private readonly ICurrentUserAccessor _currentUser;

    public EfReplicaLinkStore(
        IDbContextFactory<ZyncMasterDbContext> factory, ICurrentUserAccessor currentUser)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
    }

    public async Task<ReplicaLink> AddAsync(ReplicaLink link, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        var userId = _currentUser.UserId;

        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = new ReplicaLinkRow
        {
            Id = link.Id,
            UserId = userId,
            SourceAccountId = link.SourceAccountId,
            SourceEventId = link.SourceEventId,
            SourceGraphEventId = link.SourceGraphEventId,
            SourceKind = link.SourceKind,
            DestinationAccountId = link.DestinationAccountId,
            DestinationCalendarId = link.DestinationCalendarId,
            DestinationEventId = link.DestinationEventId,
            MaskTitle = link.MaskTitle,
            RuleId = link.RuleId,
            ContentHash = link.ContentHash,
            Status = ToStatusText(link.Status),
            CreatedUtc = link.CreatedUtc,
            UpdatedUtc = link.UpdatedUtc,
        };
        db.ReplicaLinks.Add(row);
        await db.SaveChangesAsync(ct);
        return ToDomain(row);
    }

    public async Task<ReplicaLink?> GetAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var userId = _currentUser.UserId;
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.ReplicaLinks.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == id && l.UserId == userId, ct);
        return row is null ? null : ToDomain(row);
    }

    public async Task<IReadOnlyList<ReplicaLink>> ListAsync(CancellationToken ct = default)
    {
        var userId = _currentUser.UserId;
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.ReplicaLinks.AsNoTracking()
            .Where(l => l.UserId == userId)
            .ToListAsync(ct);
        return rows.Select(ToDomain).ToList();
    }

    public async Task<IReadOnlyList<ReplicaLink>> ListBySourceEventAsync(
        string sourceEventId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceEventId);
        var userId = _currentUser.UserId;
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.ReplicaLinks.AsNoTracking()
            .Where(l => l.UserId == userId && l.SourceEventId == sourceEventId)
            .ToListAsync(ct);
        return rows.Select(ToDomain).ToList();
    }

    public async Task<bool> UpdateAsync(ReplicaLink link, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        var userId = _currentUser.UserId;

        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.ReplicaLinks
            .FirstOrDefaultAsync(l => l.Id == link.Id && l.UserId == userId, ct);
        if (row is null)
            return false;

        // Only the mutable surface: identity/source/destination-binding columns never change
        // after creation (a "moved" replica is delete + create, never an in-place rebind).
        // DestinationEventId IS mutable: the recreate flow points the link at the new event.
        row.MaskTitle = link.MaskTitle;
        row.ContentHash = link.ContentHash;
        row.Status = ToStatusText(link.Status);
        row.DestinationEventId = link.DestinationEventId;
        row.UpdatedUtc = link.UpdatedUtc;
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static string ToStatusText(ReplicaLinkStatus status) =>
        status.ToString().ToLowerInvariant();

    private static ReplicaLink ToDomain(ReplicaLinkRow r) => new()
    {
        Id = r.Id,
        UserId = r.UserId,
        SourceAccountId = r.SourceAccountId,
        SourceEventId = r.SourceEventId,
        SourceGraphEventId = r.SourceGraphEventId,
        SourceKind = r.SourceKind,
        DestinationAccountId = r.DestinationAccountId,
        DestinationCalendarId = r.DestinationCalendarId,
        DestinationEventId = r.DestinationEventId,
        MaskTitle = r.MaskTitle,
        RuleId = r.RuleId,
        ContentHash = r.ContentHash,
        Status = Enum.Parse<ReplicaLinkStatus>(r.Status, ignoreCase: true),
        CreatedUtc = r.CreatedUtc,
        UpdatedUtc = r.UpdatedUtc,
    };
}
