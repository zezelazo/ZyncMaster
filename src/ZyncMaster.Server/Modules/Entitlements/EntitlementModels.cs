namespace ZyncMaster.Server;

// Plan/entitlements seam (plan Phase 5 levers + Phase 8 billing seam + v2 §8).
//
// DESIGN DECISION (documented): today EVERYTHING is unlocked. Each lever is a plain toggle the
// user can flip; plan-based gating (Free/PRO) is wired in LATER by swapping the
// IEntitlementsService implementation in Program.cs — the engine that consumes Entitlements does
// NOT change. The default value of every member here is therefore the "unlocked" value, so a user
// with no plan and no toggles gets the full feature set.
//
// The effective entitlements for a user are computed as: plan defaults (today: everything unlocked)
// INTERSECTED with the user's per-feature toggles (what the user chose to turn off). A toggle can
// only turn a capability OFF; it can never grant more than the plan allows. That keeps the gate
// monotonic when PlanBasedEntitlementsService replaces the default one.
public sealed record Entitlements
{
    // Whether the server-side cron fallback ("Sync in the cloud") may run this user's pairs while no
    // App is running. This is the FIRST real gate (consumed by CronSyncRunner). Default true.
    public bool CloudFallbackSync { get; init; } = true;

    // Caps. int.MaxValue means "no limit" (unlocked). Reserved for future gating; not yet enforced.
    public int MaxPairs { get; init; } = int.MaxValue;
    public int MaxConnectedAccounts { get; init; } = int.MaxValue;

    // Which sync modules the user may use. All enabled by default.
    public IReadOnlyList<string> EnabledModules { get; init; } =
        new[] { "calendar", "files", "clipboard" };

    // Floor on the per-pair sync interval (minutes). 1 = effectively no floor (unlocked).
    public int MinSyncIntervalMinutes { get; init; } = 1;
}
