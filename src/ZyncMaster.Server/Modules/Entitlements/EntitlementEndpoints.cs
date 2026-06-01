using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ZyncMaster.Server.Data;

namespace ZyncMaster.Server;

// Plan/entitlements endpoints (Track C). Both REQUIRE IdentityBearer — entitlements are per
// authenticated user; the userId comes from the validated token (ICurrentUserAccessor), never a
// query parameter.
//
//   GET   /api/entitlements          -> the caller's EFFECTIVE entitlements (plan defaults ∩ toggles)
//   PATCH /api/entitlements/toggles  -> persist the caller's toggle(s) into UserToggleRow
public static class EntitlementEndpoints
{
    public static void MapEntitlementEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Effective entitlements for the caller. Defaults (today: all unlocked) intersected with
        // whatever the user turned off in their UserToggleRow.
        app.MapGet("/api/entitlements", async (
            ICurrentUserAccessor currentUser,
            IEntitlementsService entitlements,
            CancellationToken ct) =>
        {
            var effective = await entitlements.GetForUserAsync(currentUser.UserId, ct);
            return Results.Ok(new
            {
                cloudFallbackSync = effective.CloudFallbackSync,
                maxPairs = effective.MaxPairs,
                maxConnectedAccounts = effective.MaxConnectedAccounts,
                enabledModules = effective.EnabledModules,
                minSyncIntervalMinutes = effective.MinSyncIntervalMinutes,
            });
        }).RequireIdentityBearer();

        // Persist the caller's toggles. Upsert keyed on UserId — the toggle row is scoped to the
        // authenticated user, so a user can never flip another user's toggles. Today only
        // cloudFallbackSync is settable.
        app.MapPatch("/api/entitlements/toggles", async (
            HttpContext context,
            ICurrentUserAccessor currentUser,
            IDbContextFactory<ZyncMasterDbContext> factory,
            CancellationToken ct) =>
        {
            ToggleRequest? body;
            try
            {
                body = await context.Request.ReadFromJsonAsync<ToggleRequest>(ct);
            }
            catch (JsonException)
            {
                return Results.BadRequest("Invalid request body.");
            }

            if (body is null || body.CloudFallbackSync is null)
                return Results.BadRequest("Missing cloudFallbackSync.");

            var userId = currentUser.UserId;
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.UserToggles.FirstOrDefaultAsync(t => t.UserId == userId, ct);
            if (row is null)
            {
                row = new UserToggleRow { UserId = userId, CloudFallbackSync = body.CloudFallbackSync.Value };
                db.UserToggles.Add(row);
            }
            else
            {
                row.CloudFallbackSync = body.CloudFallbackSync.Value;
            }
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { cloudFallbackSync = row.CloudFallbackSync });
        }).RequireIdentityBearer();
    }

    private sealed class ToggleRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("cloudFallbackSync")]
        public bool? CloudFallbackSync { get; set; }
    }
}
