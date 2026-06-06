namespace ZyncMaster.Server;

// FIX 2 (run-lock renewal) — runs an operation while a background loop keeps the run-lock alive.
//
// The hazard this closes: a single mirror can take longer than the lock TTL when a pair has many
// calendars, deep pagination, or retries. Without renewal the lock would expire mid-run and a
// second executor (an overlapping cron tick or a manual run) could acquire it and start a CONCURRENT
// destructive sweep against the same calendar. The heartbeat periodically calls handle.RenewAsync to
// push LockedUntil forward at an interval well below the TTL, so the lock stays held for the entire
// run and is released (or expires after one TTL on a crash) afterwards.
public static class SyncRunLockHeartbeat
{
    // Fraction of the TTL between renewals. A third of the TTL gives two renewals of headroom before
    // expiry even if one renewal is delayed, while keeping write pressure minimal (one tiny UPDATE
    // every few minutes per running pair).
    public const double RenewFraction = 1.0 / 3.0;

    // Minimum heartbeat interval — guards against a pathologically small TTL turning the heartbeat
    // into a tight loop. Renewals never fire faster than this.
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(5);

    // Runs `operation` while renewing `handle` every ~ttl/3 in the background. The renewal loop is
    // cancelled as soon as the operation completes (or throws), so it never outlives the work. A
    // failed renewal (the lock was lost) is logged but does not itself abort the operation — the
    // fenced release/acquire already prevents a stale holder from clobbering the new owner; the
    // renewal simply keeps the common case (one live executor) from ever reaching that state.
    public static async Task<T> RunAsync<T>(
        ISyncRunLockHandle handle,
        TimeSpan ttl,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken ct,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(operation);

        var interval = ComputeInterval(ttl);
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = RenewLoopAsync(handle, ttl, interval, heartbeatCts.Token, logger);

        try
        {
            return await operation(ct).ConfigureAwait(false);
        }
        finally
        {
            heartbeatCts.Cancel();
            try
            {
                await heartbeat.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: the loop is cancelled when the operation finishes.
            }
        }
    }

    // Interval between renewals: ttl * RenewFraction, floored at MinInterval. Exposed via
    // ComputeInterval so tests can assert the cadence without reflection.
    public static TimeSpan ComputeInterval(TimeSpan ttl)
    {
        var candidate = TimeSpan.FromTicks((long)(ttl.Ticks * RenewFraction));
        return candidate < MinInterval ? MinInterval : candidate;
    }

    private static async Task RenewLoopAsync(
        ISyncRunLockHandle handle, TimeSpan ttl, TimeSpan interval, CancellationToken ct, ILogger? logger)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return; // operation finished — stop renewing.
            }

            var renewed = await handle.RenewAsync(ttl, ct).ConfigureAwait(false);
            if (!renewed)
            {
                // Lost the lock (expired-and-stolen, or the row vanished). Nothing more to renew;
                // log so a long-run lock loss is diagnosable, then stop.
                logger?.LogWarning(
                    "Sync run-lock for pair {PairId} could not be renewed mid-run; it may have been lost.",
                    handle.PairId);
                return;
            }
        }
    }
}
