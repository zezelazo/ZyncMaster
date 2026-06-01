using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZyncMaster.Server.Data;

namespace ZyncMaster.Server;

// Server-side cron-trigger runner (plan §D-1/§D-2). This REPLACES the Azure Functions timer /
// AlwaysOn model: an EXTERNAL scheduler (a cron on the user's VPS, or anything) makes one HTTP
// call to /api/sync/run-due and this runner executes every pair that is due RIGHT NOW and is not
// already covered by a running App.
//
// Why a dedicated cross-user runner (not ISyncPairStore): the EF stores are user-scoped through
// ICurrentUserAccessor, but the cron trigger is not a user — it must see EVERY user's pairs in one
// pass. So it queries SyncPairRows / Devices directly across all users with a single indexed query
// for the due set, then executes each pair under that pair's OWNER identity by setting the
// per-request current-user override (the same seam the OAuth callback uses), because the Graph
// token resolution downstream is user-scoped.
//
// Selection rules (documented decisions):
//   DUE      — State == "active" AND (LastRunUtc IS NULL OR LastRunUtc + IntervalMin <= now).
//              Recording LastRunUtc after a run makes a second immediate cron call idempotent: the
//              just-run pair is no longer due until its interval elapses.
//   COVERED  — the pair's owning user has ANY device with LeaseUntil > now (the App is running).
//              When covered we skip ALL of that user's pairs, including cloud<->cloud pairs: the
//              App owns syncing while it is up; cron is the FALLBACK for when no App is running.
//   COM-PIN  — a pair whose SOURCE or DESTINATION provider is OutlookCom is skipped: there is no
//              Outlook COM server-side, so its events can only flow through the device /push path.
//
// Execution is best-effort and isolated per pair: each pair runs under its own run-lock (so it can
// never collide with an App tick or a manual run), and a failure on one pair (lock busy, transient
// read, thrown writer) is recorded against that pair and does NOT abort the rest.
//
// Gating: Track C — the `cloudFallbackSync` entitlement gates the due set. A pair is skipped when
// its owner's effective entitlements have CloudFallbackSync == false. Today every user's default is
// true (everything unlocked) and only flips off when the user explicitly turns the "Sync in the
// cloud" toggle off, so the observable behaviour is unchanged unless the user opts out. The single
// clear place that consults the entitlement is IsUserGatedOutAsync below.
public sealed class CronSyncRunner
{
    private readonly IDbContextFactory<ZyncMasterDbContext> _factory;
    private readonly ISyncRunLock _runLock;
    private readonly SyncModuleRegistry _modules;
    private readonly IHttpCurrentUserOverride _userOverride;
    private readonly IEntitlementsService _entitlements;
    private readonly ServerOptions _options;
    private readonly ILogger<CronSyncRunner> _logger;

    public CronSyncRunner(
        IDbContextFactory<ZyncMasterDbContext> factory,
        ISyncRunLock runLock,
        SyncModuleRegistry modules,
        IHttpCurrentUserOverride userOverride,
        IEntitlementsService entitlements,
        IOptions<ServerOptions> options,
        ILogger<CronSyncRunner> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _runLock = runLock ?? throw new ArgumentNullException(nameof(runLock));
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _userOverride = userOverride ?? throw new ArgumentNullException(nameof(userOverride));
        _entitlements = entitlements ?? throw new ArgumentNullException(nameof(entitlements));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<RunDueSummary> RunDueAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // One indexed query for the due, active pairs across ALL users. The interval comparison is
        // done in memory (provider-portable: SQLite cannot translate AddMinutes), but the candidate
        // set is already narrowed to active pairs and ordered, so this stays cheap.
        var activePairs = await db.SyncPairs
            .AsNoTracking()
            .Where(p => p.State == "active")
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var dueRows = activePairs.Where(p => IsDue(p, now)).ToList();
        if (dueRows.Count == 0)
            return new RunDueSummary();

        // Which users currently have an active device lease (App running). Computed once for the
        // whole batch so we do not re-query per pair. The DateTimeOffset comparison is done in
        // memory because not every provider (SQLite) can translate a DateTimeOffset '>' to SQL;
        // narrowing to rows that have ANY lease keeps the materialized set small.
        var leasedRows = await db.Devices
            .AsNoTracking()
            .Where(d => d.LeaseUntil != null)
            .Select(d => new { d.UserId, d.LeaseUntil })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var covered = new HashSet<string>(
            leasedRows.Where(d => d.LeaseUntil > now).Select(d => d.UserId),
            StringComparer.Ordinal);

        var module = _modules.GetCalendar();
        var summary = new RunDueSummary();

        // Per-batch cache of the cloudFallbackSync gate so users with several due pairs are resolved
        // once, not once per pair.
        var gatedOut = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var row in dueRows)
        {
            if (ct.IsCancellationRequested)
                break;

            if (IsComPinned(row))
            {
                summary.Skipped++;
                continue;
            }

            if (covered.Contains(row.UserId))
            {
                summary.Skipped++;
                continue;
            }

            if (await IsUserGatedOutAsync(row.UserId, gatedOut, ct).ConfigureAwait(false))
            {
                summary.Skipped++;
                continue;
            }

            if (module is null)
            {
                summary.Skipped++;
                continue;
            }

            try
            {
                var ran = await RunOnePairAsync(module, row, now, ct).ConfigureAwait(false);
                if (ran)
                    summary.Ran++;
                else
                    summary.Skipped++; // lock busy: another executor already has this pair
            }
            catch (Exception ex)
            {
                // Best-effort: one pair's failure must not abort the batch.
                summary.Failed++;
                _logger.LogWarning(ex, "Cron run failed for pair {PairId}", row.Id);
            }
        }

        return summary;
    }

    // Runs a single pair under its owner's identity, guarded by the per-pair run-lock. Returns
    // false when the lock is held by another executor (the pair is skipped, not failed). The
    // current-user override makes the downstream user-scoped Graph token resolution use the pair
    // owner's connected account; it is cleared in finally so it cannot leak to the next pair.
    private async Task<bool> RunOnePairAsync(ICalendarSyncModule module, SyncPairRow row, DateTimeOffset now, CancellationToken ct)
    {
        var ttl = TimeSpan.FromMinutes(_options.SyncRunLockTtlMinutes <= 0 ? 8 : _options.SyncRunLockTtlMinutes);
        await using var handle = await _runLock
            .TryAcquireAsync(row.Id, ttl, owner: "cron", ct)
            .ConfigureAwait(false);
        if (handle is null)
            return false;

        _userOverride.Set(row.UserId);
        try
        {
            var pair = ToDomain(row);
            var (from, to) = Window(now);
            var outcome = await module.ExecuteAsync(pair, from, to, ct).ConfigureAwait(false);

            // OutlookCom sources are filtered out before we get here, so NoServerReader should not
            // occur; if it ever does, do NOT record a run (treat as a no-op, not a failure).
            if (outcome.NoServerReader)
                return false;

            await RecordRunAsync(row.Id, outcome.Result!, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _userOverride.Clear();
        }
    }

    // Records LastRunUtc (+ result) so a second immediate cron call sees the pair as no longer due.
    // Cross-user update by id: the cron context has no single owning user, so we update the row
    // directly rather than through the user-scoped store.
    private async Task RecordRunAsync(string pairId, MirrorResult result, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.SyncPairs.FirstOrDefaultAsync(p => p.Id == pairId, ct).ConfigureAwait(false);
        if (row is null)
            return;
        row.LastRunUtc = DateTimeOffset.UtcNow;
        row.LastResultJson = PairJson.Serialize(result);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // DUE = active AND (never run OR its interval has elapsed since the last run). A non-positive
    // interval is treated as "every run" (always due) so a misconfigured 0 does not silently freeze.
    public static bool IsDue(SyncPairRow row, DateTimeOffset now)
    {
        if (!string.Equals(row.State, "active", StringComparison.Ordinal))
            return false;
        if (row.LastRunUtc is null)
            return true;
        if (row.IntervalMin <= 0)
            return true;
        return row.LastRunUtc.Value.AddMinutes(row.IntervalMin) <= now;
    }

    // A pair is COM-pinned when either side is OutlookCom: there is no Outlook COM server-side, so
    // it can only sync through the device push path and must be skipped by the server-side cron.
    public static bool IsComPinned(SyncPairRow row)
    {
        var source = PairJson.Deserialize<Endpoint>(row.SourceJson);
        var dest = PairJson.Deserialize<Endpoint>(row.DestinationJson);
        return string.Equals(source.Provider, ProviderRegistry.OutlookCom, StringComparison.Ordinal)
            || string.Equals(dest.Provider, ProviderRegistry.OutlookCom, StringComparison.Ordinal);
    }

    // Track C gate: a user is gated out of the cron fallback when their effective entitlements have
    // CloudFallbackSync == false (the user turned the "Sync in the cloud" toggle off). Today the
    // default is true for everyone, so this returns false unless the user opted out. Cached per
    // batch so users with multiple due pairs are resolved once.
    private async Task<bool> IsUserGatedOutAsync(
        string userId, Dictionary<string, bool> cache, CancellationToken ct)
    {
        if (cache.TryGetValue(userId, out var gated))
            return gated;

        var entitlements = await _entitlements.GetForUserAsync(userId, ct).ConfigureAwait(false);
        gated = !entitlements.CloudFallbackSync;
        cache[userId] = gated;
        return gated;
    }

    private (DateTimeOffset from, DateTimeOffset to) Window(DateTimeOffset now)
    {
        var today = now.UtcDateTime.Date;
        var from = new DateTimeOffset(today, TimeSpan.Zero);
        return (from, from.AddDays(_options.SyncWindowDays <= 0 ? 14 : _options.SyncWindowDays));
    }

    private static SyncPair ToDomain(SyncPairRow r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        Source = PairJson.Deserialize<Endpoint>(r.SourceJson),
        Destination = PairJson.Deserialize<Endpoint>(r.DestinationJson),
        IntervalMin = r.IntervalMin,
        State = r.State,
        LastRunUtc = r.LastRunUtc,
        LastResult = r.LastResultJson is null ? null : PairJson.Deserialize<MirrorResult>(r.LastResultJson),
    };
}

// Result of a /api/sync/run-due call. Best-effort counts: Ran = pairs the cron actually executed,
// Skipped = due pairs deliberately not run (covered by an active App lease, COM-pinned, lock busy,
// or gated out), Failed = pairs whose run threw (recorded, not aborting the batch).
public sealed class RunDueSummary
{
    public int Ran { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
}
