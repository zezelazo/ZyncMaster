using System;
using System.Threading;
using System.Threading.Tasks;

namespace ZyncMaster.Engine;

// Runs a single read-and-push cycle: load the device key, read the calendar window,
// and push it to the server. The cycle never throws — failures are reported as a
// skipped SyncResult so the surrounding loop owns retry behaviour.
public sealed class SyncEngine : ISyncCycle
{
    private readonly IDeviceKeyStore _keys;
    private readonly ICalendarSource _source;
    private readonly ISyncClient _client;
    private readonly IClock _clock;
    private readonly EngineSettings _settings;

    public SyncEngine(IDeviceKeyStore keys, ICalendarSource source, ISyncClient client, IClock clock, EngineSettings settings)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public async Task<SyncResult> RunCycleAsync(CancellationToken ct = default)
    {
        var key = await _keys.LoadAsync(ct);
        if (string.IsNullOrEmpty(key))
            return new SyncResult { Skipped = true, SkipReason = "Device is not paired." };

        try
        {
            // Single source of the device read window: PairRunner.ReadWindow snaps the lower bound to
            // today 00:00 UTC, exactly matching the server's destructive sweep window
            // (PairEndpoints.Window). Using [now, +N] here instead would leave the floor at the current
            // instant, so an event starting between 00:00 UTC and `now` is inside the server sweep but
            // missing from this push — and the sweep would delete it. Both read paths (this legacy
            // single-device push and the pair-scoped PairRunner) MUST derive the window from one place.
            var (from, to) = PairRunner.ReadWindow(_clock.UtcNow, _settings.SyncWindowDays);
            // Legacy single-device push path (not pair-scoped): no per-pair selection, so pass null
            // and let OutlookComSource fall back to the device's configured calendar names.
            var events = await _source.ReadWindowAsync(from, to, null, ct);
            var push = await _client.PushAsync(key, events, ct);
            return new SyncResult { Push = push };
        }
        catch (Exception ex)
        {
            return new SyncResult { Skipped = true, SkipReason = $"Sync failed: {ex.Message}" };
        }
    }
}
