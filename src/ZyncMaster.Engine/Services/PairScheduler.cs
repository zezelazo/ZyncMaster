using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ZyncMaster.Core;

namespace ZyncMaster.Engine;

// Drives all configured sync pairs on their individual cadences from a single
// ticking loop. Each tick reloads the pair list from the server (so newly created,
// paused, and deleted pairs take effect), then runs every active pair that is due.
//
// A COM-sourced pair (Source.Provider == "OutlookCom") is read locally through
// ICalendarSource and pushed to the server; every other pair is mirrored entirely
// server-side via RunPair. Per-pair failures are isolated so one bad pair never
// stops the others, and the loop only exits on cancellation.
//
// Listing the pairs is HUMAN-only on the server (RequireCookieOrIdentityBearer), so the tick
// reads the pair list with the signed-in user's IDENTITY BEARER (via IIdentityTokenProvider) —
// NOT the device api key. The push (COM data path) and run still authenticate with the device
// API KEY. A tick with no identity (signed out) is a clean no-op, exactly like no device key.
public sealed class PairScheduler
{
    private static readonly TimeSpan DefaultTickInterval = TimeSpan.FromSeconds(30);

    private readonly IPairsClient _client;
    private readonly ICalendarSource _comSource;
    private readonly IDeviceKeyStore _keys;
    private readonly IIdentityTokenProvider _identity;
    private readonly IClock _clock;
    private readonly EngineSettings _settings;
    private readonly IAppLogger _logger;
    private readonly TimeSpan _tickInterval;

    // Per-pair next-due time. Pairs absent from the latest ListPairs are dropped.
    private readonly Dictionary<string, DateTimeOffset> _nextRun = new();

    // Track B — the SyncRequestedUtc stamp this device's scheduler has already acted on, per pair.
    // A pair whose server-side SyncRequestedUtc is newer than the value here is run immediately
    // (in addition to its due interval); after a run the server clears the stamp on RecordRunAsync,
    // so the next tick sees null and does not re-run. Pairs absent from the latest list are dropped.
    private readonly Dictionary<string, DateTimeOffset> _lastHandledRequest = new();

    // Track B — this device's own deviceId, resolved lazily from the device api key (GET
    // /api/devices/me) and cached. Needed to filter COM-pinned pairs: a device only runs the COM
    // pairs pinned to itself. Null until first resolved; a resolution failure leaves it null and is
    // retried next tick (the device simply runs nothing COM-pinned-to-others, which is safe).
    private string? _deviceId;

    public PairScheduler(
        IPairsClient client,
        ICalendarSource comSource,
        IDeviceKeyStore keys,
        IIdentityTokenProvider identity,
        IClock clock,
        EngineSettings settings,
        IAppLogger? logger = null,
        TimeSpan? tickInterval = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _comSource = comSource ?? throw new ArgumentNullException(nameof(comSource));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? NullAppLogger.Instance;
        _tickInterval = tickInterval ?? DefaultTickInterval;
        if (_tickInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(tickInterval), "Tick interval must be greater than zero.");
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await TickSafelyAsync(ct);

        using var timer = new PeriodicTimer(_tickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await TickSafelyAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation — exit cleanly.
        }
    }

    // One scheduling pass: reload pairs, run those active pairs that are due.
    // Exposed for testing with a controllable clock + fakes.
    public async Task TickAsync(CancellationToken ct)
    {
        var apiKey = await _keys.LoadAsync(ct);
        if (string.IsNullOrEmpty(apiKey))
            return;

        // Listing pairs is human-only on the server; it needs the signed-in user's identity
        // bearer, not the device key. Signed out -> nothing to schedule this tick.
        var bearer = await _identity.LoadAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(bearer))
            return;

        IReadOnlyList<SyncPair> pairs;
        try
        {
            pairs = await _client.ListPairsAsync(bearer, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Server unreachable this tick — keep the existing schedule and retry next tick. A
            // transient (DNS after sleep/resume, reset during a deploy, timeout) logs one concise
            // line; only an unexpected failure earns the full exception.
            var transient = TransientNetworkError.Describe(ex);
            if (transient is not null)
                _logger.Log(LogLevel.Warning, $"Scheduler tick: server unreachable ({transient}); retrying next tick.");
            else
                _logger.Log(LogLevel.Warning, "Scheduler tick: could not list pairs (server unreachable?).", ex);
            return;
        }

        // Resolve this device's id once (cached) so we can filter COM-pinned pairs to ourselves.
        // A failure leaves it null: we then skip every COM pair pinned to a non-null other device,
        // which is the safe default (we never compete for a pair we may not own).
        await EnsureDeviceIdAsync(apiKey, ct);

        var now = _clock.UtcNow;
        var seen = new HashSet<string>();

        _logger.Log(LogLevel.Info, $"Scheduler tick: {pairs.Count} pair(s) listed.");

        foreach (var pair in pairs)
        {
            seen.Add(pair.Id);

            if (!IsActive(pair.State))
            {
                _logger.Log(LogLevel.Debug, $"Pair '{pair.Id}': skip (state={pair.State}).");
                continue;
            }

            // Track B — COM device-pinning filter. A COM-sourced pair may only be read by the device
            // it is pinned to. Skip any COM pair pinned to a DIFFERENT device. A COM pair with no pin
            // yet (PinnedDeviceId == null) is claimed on first run by whichever device runs it (the
            // server stamps the pin on first /push), so it is NOT skipped here. Non-COM pairs are
            // server-side and unaffected.
            if (PairRunner.IsOutlookCom(pair)
                && !string.IsNullOrEmpty(pair.PinnedDeviceId)
                && !string.Equals(pair.PinnedDeviceId, _deviceId, StringComparison.Ordinal))
            {
                // When _deviceId is null (GET /devices/me failed) every COM-pinned pair is skipped
                // here, including ones pinned to THIS device — so a persistent resolution failure
                // silently freezes all of this device's COM sync. Surface that at Warning so it is
                // diagnosable; a genuine pin-to-another-device (id resolved, just not ours) stays Debug.
                if (string.IsNullOrEmpty(_deviceId))
                    _logger.Log(LogLevel.Warning, $"Pair '{pair.Id}': skip (COM pinned, but this device's id is unresolved — /devices/me failing?).");
                else
                    _logger.Log(LogLevel.Debug, $"Pair '{pair.Id}': skip (COM pinned to another device).");
                continue;
            }

            // Track B — sync-now signal. The pinned device runs the pair immediately when the server's
            // SyncRequestedUtc is newer than the stamp this device last acted on, even if the interval
            // is not due yet. The server clears the stamp on the recorded run, so this fires once.
            var signalled = HasFreshSyncRequest(pair);

            if (!signalled && _nextRun.TryGetValue(pair.Id, out var due) && now < due)
            {
                _logger.Log(LogLevel.Debug, $"Pair '{pair.Id}': skip (not due until {due:o}).");
                continue;
            }

            // Record that we are handling this signal stamp so a later tick (before the server clears
            // it) does not run the pair a second time. The server-side clear is the durable reset;
            // this in-memory mark covers the window between our run and that clear.
            if (signalled && pair.SyncRequestedUtc is { } stamp)
            {
                _lastHandledRequest[pair.Id] = stamp;
                _logger.Log(LogLevel.Info, $"Pair '{pair.Id}': sync-now signal observed, running immediately.");
            }

            try
            {
                _logger.Log(LogLevel.Debug, $"Pair '{pair.Id}': due, running.");
                await RunPairAsync(apiKey, pair, now, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Isolate per-pair failures so one bad pair never stops the others.
                _logger.Log(LogLevel.Error, $"Pair '{pair.Id}' ({pair.Name}): sync failed.", ex);
            }

            // Schedule the next run regardless of success so a failing pair retries on its cadence.
            _nextRun[pair.Id] = now + IntervalOf(pair);
        }

        // Drop bookkeeping for pairs the server no longer reports.
        DropMissing(seen);
    }

    private Task RunPairAsync(string apiKey, SyncPair pair, DateTimeOffset now, CancellationToken ct)
        => PairRunner.RunOnceAsync(_client, _comSource, pair, apiKey, now, _settings, ct, _logger);

    private async Task TickSafelyAsync(CancellationToken ct)
    {
        try
        {
            await TickAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never let a tick kill the loop.
            _logger.Log(LogLevel.Error, "Scheduler tick failed unexpectedly.", ex);
        }
    }

    private void DropMissing(HashSet<string> seen)
    {
        List<string>? stale = null;
        foreach (var id in _nextRun.Keys)
            if (!seen.Contains(id))
                (stale ??= new List<string>()).Add(id);

        if (stale != null)
            foreach (var id in stale)
                _nextRun.Remove(id);

        // Drop sync-now bookkeeping for the same vanished pairs so the dictionary cannot grow
        // unbounded across deletes/re-creates.
        List<string>? staleReq = null;
        foreach (var id in _lastHandledRequest.Keys)
            if (!seen.Contains(id))
                (staleReq ??= new List<string>()).Add(id);

        if (staleReq != null)
            foreach (var id in staleReq)
                _lastHandledRequest.Remove(id);
    }

    // Resolves and caches this device's id from the device api key. Best-effort: a failure (server
    // briefly unreachable, key not yet usable) leaves _deviceId null and is retried next tick.
    private async Task EnsureDeviceIdAsync(string apiKey, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_deviceId))
            return;

        try
        {
            var me = await _client.GetDeviceMeAsync(apiKey, ct);
            if (!string.IsNullOrEmpty(me.DeviceId))
                _deviceId = me.DeviceId;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Warning, "Scheduler tick: could not resolve this device's id (server unreachable?).", ex);
        }
    }

    // True when the pair carries a sync-now stamp this device has NOT yet acted on. A pair with no
    // stamp, or one whose stamp we already handled (== last handled), returns false.
    private bool HasFreshSyncRequest(SyncPair pair)
    {
        if (pair.SyncRequestedUtc is not { } requested)
            return false;
        return !_lastHandledRequest.TryGetValue(pair.Id, out var handled) || requested > handled;
    }

    private static bool IsActive(string state)
        => string.Equals(state, "active", StringComparison.OrdinalIgnoreCase);

    private TimeSpan IntervalOf(SyncPair pair)
    {
        var minutes = pair.IntervalMin > 0 ? pair.IntervalMin : _settings.IntervalMinutes;
        if (minutes <= 0) minutes = 10;
        return TimeSpan.FromMinutes(minutes);
    }
}
