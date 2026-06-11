using Microsoft.EntityFrameworkCore;
using ZyncMaster.Server.Data;

namespace ZyncMaster.Server;

// EF-backed prefix rule store. User-scoped like EfReplicaLinkStore. The destination list is
// stored relationally (PrefixRuleDestinations) and REPLACED wholesale on update: membership in
// that list IS the per-calendar two-way flag (spec §2/§5), so the list is the unit of edit.
public sealed class EfPrefixRuleStore : IPrefixRuleStore
{
    private readonly IDbContextFactory<ZyncMasterDbContext> _factory;
    private readonly ICurrentUserAccessor _currentUser;

    public EfPrefixRuleStore(
        IDbContextFactory<ZyncMasterDbContext> factory, ICurrentUserAccessor currentUser)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _currentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
    }

    public async Task<PrefixRule> AddAsync(PrefixRule rule, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var userId = _currentUser.UserId;

        await using var db = await _factory.CreateDbContextAsync(ct);
        db.PrefixRules.Add(new PrefixRuleRow
        {
            Id = rule.Id,
            UserId = userId,
            Prefix = rule.Prefix,
            MaskTitle = rule.MaskTitle,
            Enabled = rule.Enabled,
            SortOrder = rule.SortOrder,
        });
        foreach (var d in rule.Destinations)
        {
            db.PrefixRuleDestinations.Add(new PrefixRuleDestinationRow
            {
                Id = Guid.NewGuid().ToString("N"),
                RuleId = rule.Id,
                AccountId = d.AccountId,
                CalendarId = d.CalendarId,
            });
        }
        await db.SaveChangesAsync(ct);
        return rule with { UserId = userId };
    }

    public async Task<PrefixRule?> GetAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var userId = _currentUser.UserId;
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.PrefixRules.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId, ct);
        if (row is null)
            return null;
        var dests = await db.PrefixRuleDestinations.AsNoTracking()
            .Where(d => d.RuleId == id)
            .ToListAsync(ct);
        return ToDomain(row, dests);
    }

    public async Task<IReadOnlyList<PrefixRule>> ListAsync(CancellationToken ct = default)
    {
        var userId = _currentUser.UserId;
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.PrefixRules.AsNoTracking()
            .Where(r => r.UserId == userId)
            .OrderBy(r => r.SortOrder)
            .ToListAsync(ct);
        if (rows.Count == 0)
            return Array.Empty<PrefixRule>();

        var ids = rows.Select(r => r.Id).ToList();
        var dests = await db.PrefixRuleDestinations.AsNoTracking()
            .Where(d => ids.Contains(d.RuleId))
            .ToListAsync(ct);
        var byRule = dests.ToLookup(d => d.RuleId, StringComparer.Ordinal);
        return rows.Select(r => ToDomain(r, byRule[r.Id].ToList())).ToList();
    }

    public async Task<bool> UpdateAsync(PrefixRule rule, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var userId = _currentUser.UserId;

        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.PrefixRules
            .FirstOrDefaultAsync(r => r.Id == rule.Id && r.UserId == userId, ct);
        if (row is null)
            return false;

        row.Prefix = rule.Prefix;
        row.MaskTitle = rule.MaskTitle;
        row.Enabled = rule.Enabled;
        row.SortOrder = rule.SortOrder;

        // Replace the destination list wholesale — adding/removing a calendar here IS
        // enabling/disabling the two-way for this rule.
        var old = await db.PrefixRuleDestinations.Where(d => d.RuleId == rule.Id).ToListAsync(ct);
        db.PrefixRuleDestinations.RemoveRange(old);
        foreach (var d in rule.Destinations)
        {
            db.PrefixRuleDestinations.Add(new PrefixRuleDestinationRow
            {
                Id = Guid.NewGuid().ToString("N"),
                RuleId = rule.Id,
                AccountId = d.AccountId,
                CalendarId = d.CalendarId,
            });
        }
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RemoveAsync(string id, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var userId = _currentUser.UserId;

        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.PrefixRules
            .FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId, ct);
        if (row is null)
            return false;

        // SQLite EnsureCreated honors the cascade, but delete the children explicitly so the
        // behavior never depends on provider cascade semantics.
        var dests = await db.PrefixRuleDestinations.Where(d => d.RuleId == id).ToListAsync(ct);
        db.PrefixRuleDestinations.RemoveRange(dests);
        db.PrefixRules.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static PrefixRule ToDomain(
        PrefixRuleRow r, IReadOnlyList<PrefixRuleDestinationRow> dests) => new()
    {
        Id = r.Id,
        UserId = r.UserId,
        Prefix = r.Prefix,
        MaskTitle = r.MaskTitle,
        Enabled = r.Enabled,
        SortOrder = r.SortOrder,
        Destinations = dests.Select(d => new PrefixRuleDestination(d.AccountId, d.CalendarId)).ToList(),
    };
}
