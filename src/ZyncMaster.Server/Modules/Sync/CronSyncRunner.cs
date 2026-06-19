using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZyncMaster.Graph;
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
    private readonly SyncBroadcaster _broadcaster;
    private readonly ServerOptions _options;
    private readonly ILogger<CronSyncRunner> _logger;

    public CronSyncRunner(
        IDbContextFactory<ZyncMasterDbContext> factory,
        ISyncRunLock runLock,
        SyncModuleRegistry modules,
        IHttpCurrentUserOverride userOverride,
        IEntitlementsService entitlements,
        SyncBroadcaster broadcaster,
        IOptions<ServerOptions> options,
        ILogger<CronSyncRunner> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _runLock = runLock ?? throw new ArgumentNullException(nameof(runLock));
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));
        _userOverride = userOverride ?? throw new ArgumentNullException(nameof(userOverride));
        _entitlements = entitlements ?? throw new ArgumentNullException(nameof(entitlements));
        _broadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
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
        {
            // FIX 5 — even a no-op run logs a one-line summary at Info so a "nothing syncs" report
            // can be diagnosed from the logs alone (here: zero pairs were due).
            _logger.LogInformation(
                "Cron run-due: {Active} active pair(s), {Due} due, nothing to run.",
                activePairs.Count, dueRows.Count);
            return new RunDueSummary();
        }

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

        // FIX 5 — break the skip count down by REASON so the per-run summary can explain why a due
        // pair did not run ("covered" by a live App vs "gated" by the entitlement vs "lock busy").
        int covered_ = 0, comPinned = 0, gated = 0, lockBusy = 0;

        foreach (var row in dueRows)
        {
            if (ct.IsCancellationRequested)
                break;

            if (IsComPinned(row))
            {
                summary.Skipped++;
                comPinned++;
                _logger.LogDebug("Cron run-due: pair {PairId} skipped (COM-pinned, no server reader).", row.Id);
                continue;
            }

            if (covered.Contains(row.UserId))
            {
                summary.Skipped++;
                covered_++;
                _logger.LogDebug("Cron run-due: pair {PairId} skipped (covered by a running App lease).", row.Id);
                continue;
            }

            if (await IsUserGatedOutAsync(row.UserId, gatedOut, ct).ConfigureAwait(false))
            {
                summary.Skipped++;
                gated++;
                _logger.LogDebug("Cron run-due: pair {PairId} skipped (cloud fallback gated off).", row.Id);
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
                {
                    summary.Ran++;
                    _logger.LogInformation("Cron run-due: pair {PairId} synced.", row.Id);
                }
                else
                {
                    summary.Skipped++; // lock busy: another executor already has this pair
                    lockBusy++;
                    _logger.LogDebug("Cron run-due: pair {PairId} skipped (run-lock busy).", row.Id);
                }
            }
            catch (Exception ex)
            {
                // Best-effort: one pair's failure must not abort the batch.
                summary.Failed++;
                _logger.LogWarning(ex, "Cron run failed for pair {PairId}", row.Id);
            }
        }

        // FIX 5 — one line per run summarising the outcome, so "it doesn't sync" can be diagnosed
        // from the logs alone (due/ran/skipped with the skip reasons broken out). Escalated to
        // Warning when ANY pair failed, so a log-based alert (or a `grep [Warning]`) catches a run
        // that completed-but-failed — the endpoint itself still returns 200 (it ran fine; the failures
        // are per-pair data), so the Warning line is the monitoring signal.
        var summaryTemplate =
            "Cron run-due summary: due={Due} ran={Ran} skipped={Skipped} failed={Failed} " +
            "(covered={Covered} comPinned={ComPinned} gated={Gated} lockBusy={LockBusy}).";
        if (summary.Failed > 0)
            _logger.LogWarning(summaryTemplate,
                dueRows.Count, summary.Ran, summary.Skipped, summary.Failed,
                covered_, comPinned, gated, lockBusy);
        else
            _logger.LogInformation(summaryTemplate,
                dueRows.Count, summary.Ran, summary.Skipped, summary.Failed,
                covered_, comPinned, gated, lockBusy);

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

            try
            {
                // FIX 2 — renew the run-lock while this pair's (possibly long) read+mirror runs so
                // the lock cannot expire mid-run and let another executor (an overlapping cron tick
                // or a manual run) start a concurrent destructive sweep against the same calendar.
                var outcome = await SyncRunLockHeartbeat.RunAsync(
                    handle, ttl,
                    token => module.ExecuteAsync(pair, from, to, token),
                    ct, _logger).ConfigureAwait(false);

                // OutlookCom sources are filtered out before we get here, so NoServerReader should
                // not occur; if it ever does, do NOT record a run (treat as a no-op, not a failure).
                if (outcome.NoServerReader)
                    return false;

                await RecordRunAsync(row.Id, row.UserId, outcome.Result!, ct).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Batch cancellation — propagate so the caller stops; do NOT record (the run did not
                // complete and the next batch should reconsider the pair as it was).
                throw;
            }
            catch (Exception ex) when (Classify(ex) != SyncErrorKind.Transient)
            {
                // FIX E — a NON-transitory failure (a revoked/expired token surfacing as
                // UserRecoverable, or a Fatal contract error) would otherwise leave LastRunUtc
                // untouched, so the pair stays DUE and the cron hammers Graph/the IdP every tick.
                // Record LastRunUtc + a failed result so the pair backs off to its normal interval.
                // Only a TRULY transient escape (handled by the outer when-filter NOT matching)
                // skips recording, so a genuine throttle still retries promptly.
                await RecordRunAsync(row.Id, row.UserId, FailedRunResult(ex), ct).ConfigureAwait(false);

                // Re-throw so the batch still counts this as Failed (RunDueAsync's catch). The
                // LastRunUtc is now advanced, so the immediate next tick will not re-run the pair.
                throw;
            }
        }
        finally
        {
            _userOverride.Clear();
        }
    }

    // Records a failed run as a non-partial MirrorResult carrying the error. Partial=false (this was
    // NOT a transient short-read deferral — it is a hard failure that must back off to the interval),
    // with the error surfaced in Failures so the panel can show why the last run did not apply.
    private static MirrorResult FailedRunResult(Exception ex)
        => new MirrorResult { Failures = new List<string> { $"Run failed: {ex.Message}" } };

    // Thin wrapper over the shared classifier. Real cancellation is already filtered out by the
    // earlier OperationCanceledException catch, so this only ever sees genuine run failures.
    private static SyncErrorKind Classify(Exception ex) => SyncErrorClassifier.Classify(ex);

    // Records LastRunUtc (+ result) so a second immediate cron call sees the pair as no longer due,
    // then fans the run out over the WS to that pair OWNER's live sessions so any open App/panel
    // refreshes the result in real time. Cross-user update by id: the cron context has no single
    // owning user, so we update the row directly rather than through the user-scoped store; the owner
    // for the broadcast is the row's UserId. The cron is not one of the user's devices, so no origin
    // is excluded (originDeviceId empty) and the run reaches all of the owner's sessions. Best-effort:
    // a dead peer socket never fails the cron run.
    private async Task RecordRunAsync(string pairId, string userId, MirrorResult result, CancellationToken ct)
    {
        DateTimeOffset recordedUtc;
        await using (var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            var row = await db.SyncPairs.FirstOrDefaultAsync(p => p.Id == pairId, ct).ConfigureAwait(false);
            if (row is null)
                return;
            recordedUtc = DateTimeOffset.UtcNow;
            row.LastRunUtc = recordedUtc;
            row.LastResultJson = PairJson.Serialize(result);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        await _broadcaster
            .BroadcastPairRunAsync(userId, originDeviceId: string.Empty, pairId, result, recordedUtc, ct)
            .ConfigureAwait(false);
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

    // A pair is COM-pinned when its SOURCE is OutlookCom: there is no Outlook COM server-side reader,
    // so it can only sync through the device push path and must be skipped by the server-side cron. The
    // COM side is always the source (no COM writer exists; the destination is always Graph). Detection
    // is shared verbatim with PairEndpoints.IsComPinnedPair and PairRunner.IsOutlookCom — all three use
    // source-only with OrdinalIgnoreCase and must agree exactly.
    public static bool IsComPinned(SyncPairRow row)
    {
        var source = PairJson.Deserialize<Endpoint>(row.SourceJson);
        return string.Equals(source.Provider, ProviderRegistry.OutlookCom, StringComparison.OrdinalIgnoreCase);
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
        PinnedDeviceId = r.PinnedDeviceId,
        SyncRequestedUtc = r.SyncRequestedUtc,
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
