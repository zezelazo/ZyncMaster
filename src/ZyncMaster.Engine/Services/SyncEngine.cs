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
            var from = _clock.UtcNow;
            var to = from.AddDays(_settings.SyncWindowDays);
            var events = await _source.ReadWindowAsync(from, to, ct);
            var push = await _client.PushAsync(key, events, ct);
            return new SyncResult { Push = push };
        }
        catch (Exception ex)
        {
            return new SyncResult { Skipped = true, SkipReason = $"Sync failed: {ex.Message}" };
        }
    }
}
