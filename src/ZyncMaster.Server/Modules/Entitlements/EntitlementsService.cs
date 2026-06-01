using Microsoft.EntityFrameworkCore;
using ZyncMaster.Server.Data;

namespace ZyncMaster.Server;

// Resolves the EFFECTIVE entitlements for a user: the plan defaults INTERSECTED with the user's
// per-feature toggles. Takes a userId explicitly (not the ambient ICurrentUserAccessor) so the
// cross-user cron runner can ask for any pair owner's entitlements in one pass.
public interface IEntitlementsService
{
    Task<Entitlements> GetForUserAsync(string userId, CancellationToken ct = default);
}

// Today's implementation: plan defaults are "everything unlocked" (a bare new Entitlements()), then
// the user's UserToggleRow can only turn capabilities OFF. There is no plan lookup yet.
//
// SWAP POINT (Track C / Phase 8): when plans (Free/PRO) go live, register a
// PlanBasedEntitlementsService in Program.cs INSTEAD of this one. That implementation will read the
// user's plan slug (UserRow.Plan) to build the plan-specific defaults, then apply the SAME toggle
// intersection. Nothing downstream (CronSyncRunner, endpoints) changes — only this one DI line.
public sealed class DefaultEntitlementsService : IEntitlementsService
{
    private readonly IDbContextFactory<ZyncMasterDbContext> _factory;

    public DefaultEntitlementsService(IDbContextFactory<ZyncMasterDbContext> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public async Task<Entitlements> GetForUserAsync(string userId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);

        // Plan defaults — today everything is unlocked.
        var defaults = new Entitlements();

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var toggle = await db.UserToggles
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.UserId == userId, ct)
            .ConfigureAwait(false);

        // No row -> the user never changed any toggle -> pure defaults (everything unlocked).
        if (toggle is null)
            return defaults;

        // Intersection: a toggle can only turn a capability OFF, never grant more than the plan.
        return defaults with
        {
            CloudFallbackSync = defaults.CloudFallbackSync && toggle.CloudFallbackSync,
        };
    }
}
