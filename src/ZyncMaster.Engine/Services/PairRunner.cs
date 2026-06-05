using System;
using System.Threading;
using System.Threading.Tasks;

namespace ZyncMaster.Engine;

// Shared "run one pair now" logic for the two device-side drivers that need it:
// the background PairScheduler (on each due tick) and the App's "Sync now" button
// (EngineActions.RunPairNowAsync). Both must treat an OutlookCom source the same way —
// the server has no local COM reader, so a COM-sourced pair is read locally through
// ICalendarSource and pushed; every other pair is mirrored entirely server-side via RunPair.
//
// Keeping this in one place avoids the two call sites drifting on the COM-vs-Graph
// decision or the read window.
public static class PairRunner
{
    public const string OutlookComProvider = "OutlookCom";

    // Runs the given pair once and returns the server's MirrorResult.
    //
    // DATA-INTEGRITY CONTRACT (read-from == sweep-from): the server's destructive orphan sweep
    // (PairEndpoints.Window) reconciles the window [today 00:00 UTC, +SyncWindowDays]. The COM
    // read MUST cover that SAME lower bound, or any event that starts between 00:00 UTC and `now`
    // would be inside the sweep window but absent from the pushed set — and the sweep would delete
    // it from the destination even though it still exists at the source. So the COM window's lower
    // bound is today's date at 00:00 UTC (NOT the current instant `now`), exactly matching the
    // server's Window(). `now` is still the scheduling clock the callers pass; only the read floor
    // is snapped to the day boundary. The upper bound stays today 00:00 UTC + SyncWindowDays so the
    // whole [from, to] read window is identical to the server's sweep window.
    public static async Task<MirrorResult> RunOnceAsync(
        IPairsClient client,
        ICalendarSource comSource,
        SyncPair pair,
        string apiKey,
        DateTimeOffset now,
        EngineSettings settings,
        CancellationToken ct)
    {
        if (client == null) throw new ArgumentNullException(nameof(client));
        if (comSource == null) throw new ArgumentNullException(nameof(comSource));
        if (pair == null) throw new ArgumentNullException(nameof(pair));
        if (apiKey == null) throw new ArgumentNullException(nameof(apiKey));
        if (settings == null) throw new ArgumentNullException(nameof(settings));

        if (IsOutlookCom(pair))
        {
            var (from, to) = ReadWindow(now, settings.SyncWindowDays);
            var events = await comSource.ReadWindowAsync(from, to, ct);
            return await client.PushPairAsync(apiKey, pair.Id, events, ct);
        }

        return await client.RunPairAsync(apiKey, pair.Id, ct);
    }

    // The COM read window, snapped to the day boundary so its lower bound matches the server's
    // sweep window (PairEndpoints.Window): [today 00:00 UTC, today 00:00 UTC + windowDays].
    // Public + static so a test can assert the floor without standing up the whole runner.
    public static (DateTimeOffset from, DateTimeOffset to) ReadWindow(DateTimeOffset now, int windowDays)
    {
        var from = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        return (from, from.AddDays(windowDays));
    }

    public static bool IsOutlookCom(SyncPair pair)
        => pair != null
           && string.Equals(pair.Source.Provider, OutlookComProvider, StringComparison.OrdinalIgnoreCase);
}
