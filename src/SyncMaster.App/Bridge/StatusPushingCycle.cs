using System;
using System.Threading;
using System.Threading.Tasks;
using SyncMaster.App.State;
using SyncMaster.Engine;

namespace SyncMaster.App.Bridge;

// Wraps the real ISyncCycle so the SyncLoop participates in the app's status pipeline:
//   - when the user has paused auto-sync, the cycle is skipped (no server call) and a
//     Paused status is pushed;
//   - otherwise the inner cycle runs, its result is recorded on EngineActions, and the
//     resulting AppStatus is pushed to the web layer and the tray.
// This keeps SyncLoop unchanged (it just drives an ISyncCycle on the interval) while the
// app gets live status after every tick.
public sealed class StatusPushingCycle : ISyncCycle
{
    private readonly ISyncCycle _inner;
    private readonly EngineActions _actions;
    private readonly UiBridge _bridge;
    private readonly Action<SyncStatus>? _onStatus;

    public StatusPushingCycle(ISyncCycle inner, EngineActions actions, UiBridge bridge, Action<SyncStatus>? onStatus = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _onStatus = onStatus;
    }

    public async Task<SyncResult> RunCycleAsync(CancellationToken ct = default)
    {
        if (_actions.IsPaused)
        {
            await PublishAsync(ct);
            return new SyncResult { Skipped = true, SkipReason = "Auto-sync is paused." };
        }

        SyncResult result;
        try
        {
            result = await _inner.RunCycleAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }

        _actions.RecordResult(result);
        await PublishAsync(ct);
        return result;
    }

    private async Task PublishAsync(CancellationToken ct)
    {
        var status = await _actions.GetStatusAsync(ct);
        _bridge.PushStatus(status);
        _onStatus?.Invoke(status.Status);
    }
}
