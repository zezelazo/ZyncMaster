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
    private const string OutlookComProvider = "OutlookCom";
    private static readonly TimeSpan DefaultTickInterval = TimeSpan.FromSeconds(30);

    private readonly IPairsClient _client;
    private readonly ICalendarSource _comSource;
    private readonly IDeviceKeyStore _keys;
    private readonly IIdentityTokenProvider _identity;
    private readonly IClock _clock;
    private readonly EngineSettings _settings;
    private readonly TimeSpan _tickInterval;

    // Per-pair next-due time. Pairs absent from the latest ListPairs are dropped.
    private readonly Dictionary<string, DateTimeOffset> _nextRun = new();

    public PairScheduler(
        IPairsClient client,
        ICalendarSource comSource,
        IDeviceKeyStore keys,
        IIdentityTokenProvider identity,
        IClock clock,
        EngineSettings settings,
        TimeSpan? tickInterval = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _comSource = comSource ?? throw new ArgumentNullException(nameof(comSource));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
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
        catch
        {
            // Server unreachable this tick — keep the existing schedule and retry next tick.
            return;
        }

        var now = _clock.UtcNow;
        var seen = new HashSet<string>();

        foreach (var pair in pairs)
        {
            seen.Add(pair.Id);

            if (!IsActive(pair.State))
                continue;

            if (_nextRun.TryGetValue(pair.Id, out var due) && now < due)
                continue;

            try
            {
                await RunPairAsync(apiKey, pair, now, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Isolate per-pair failures so one bad pair never stops the others.
            }

            // Schedule the next run regardless of success so a failing pair retries on its cadence.
            _nextRun[pair.Id] = now + IntervalOf(pair);
        }

        // Drop bookkeeping for pairs the server no longer reports.
        DropMissing(seen);
    }

    private async Task RunPairAsync(string apiKey, SyncPair pair, DateTimeOffset now, CancellationToken ct)
    {
        if (string.Equals(pair.Source.Provider, OutlookComProvider, StringComparison.OrdinalIgnoreCase))
        {
            var to = now.AddDays(_settings.SyncWindowDays);
            var events = await _comSource.ReadWindowAsync(now, to, ct);
            await _client.PushPairAsync(apiKey, pair.Id, events, ct);
        }
        else
        {
            await _client.RunPairAsync(apiKey, pair.Id, ct);
        }
    }

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
        catch
        {
            // Never let a tick kill the loop.
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
