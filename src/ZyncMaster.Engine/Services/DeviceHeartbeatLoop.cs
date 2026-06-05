using System;
using System.Threading;
using System.Threading.Tasks;
using ZyncMaster.Core;

namespace ZyncMaster.Engine;

// FIX C (client) — keeps the device's server-side lease alive while the App is running.
//
// The server treats a device with LeaseUntil > now as "App running" and skips that user's pairs in
// the cron fallback (CronSyncRunner). The lease is set on register and renewed on every /push, but a
// configured-but-idle App (no due COM pairs to push for a while) would otherwise let the lease lapse
// and hand its syncs to cron, causing a double run. This loop sends an explicit heartbeat on a
// PeriodicTimer comfortably inside the lease TTL so the lease never lapses while the App is up.
//
// A tick with no device key (not yet paired) is a clean no-op; a failed heartbeat (server briefly
// unreachable) is swallowed and retried next tick — exactly like PairScheduler's per-tick isolation.
public sealed class DeviceHeartbeatLoop
{
    // Default cadence: 4 minutes, comfortably inside the server's default 10-minute lease TTL so a
    // single missed beat (transient network) does not immediately expire the lease.
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(4);

    private readonly IPairsClient _client;
    private readonly IDeviceKeyStore _keys;
    private readonly IAppLogger _logger;
    private readonly TimeSpan _interval;

    public DeviceHeartbeatLoop(IPairsClient client, IDeviceKeyStore keys, TimeSpan? interval = null, IAppLogger? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _logger = logger ?? NullAppLogger.Instance;
        _interval = interval ?? DefaultInterval;
        if (_interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), "Interval must be greater than zero.");
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // Beat once up front so a freshly-started App refreshes its lease immediately instead of
        // waiting a full interval (its register lease may already be partway through its TTL).
        await BeatSafelyAsync(ct).ConfigureAwait(false);

        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await BeatSafelyAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown — exit cleanly.
        }
    }

    // One heartbeat: load the device key (no key -> not paired yet -> no-op) and renew the lease.
    // Exposed for testing with fakes + a controllable cancellation token.
    public async Task BeatAsync(CancellationToken ct)
    {
        var apiKey = await _keys.LoadAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(apiKey))
            return;

        _logger.Log(LogLevel.Debug, "Device heartbeat: renewing lease.");
        await _client.HeartbeatAsync(apiKey, ct).ConfigureAwait(false);
    }

    private async Task BeatSafelyAsync(CancellationToken ct)
    {
        try
        {
            await BeatAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Server unreachable or transient failure this tick — keep the loop alive and retry.
        }
    }
}
