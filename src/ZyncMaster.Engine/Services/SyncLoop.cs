using System;
using System.Threading;
using System.Threading.Tasks;

namespace ZyncMaster.Engine;

// Drives an ISyncCycle on a fixed interval. Runs one cycle immediately, then once per
// timer tick. A failing cycle never kills the loop — per-cycle exceptions are swallowed
// so transient errors (Outlook closed, server down) just retry on the next tick. The
// loop exits cleanly when the token is cancelled.
public sealed class SyncLoop
{
    private readonly ISyncCycle _cycle;
    private readonly TimeSpan _interval;

    public SyncLoop(ISyncCycle cycle, TimeSpan interval)
    {
        _cycle = cycle ?? throw new ArgumentNullException(nameof(cycle));
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), "Interval must be greater than zero.");
        _interval = interval;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        await RunOneCycleAsync(ct);

        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await RunOneCycleAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation — exit cleanly.
        }
    }

    private async Task RunOneCycleAsync(CancellationToken ct)
    {
        try
        {
            await _cycle.RunCycleAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Swallow per-cycle failures so one bad cycle never kills the loop.
        }
    }
}
