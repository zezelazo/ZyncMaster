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
// decision or the [now, now + SyncWindowDays] window.
public static class PairRunner
{
    public const string OutlookComProvider = "OutlookCom";

    // Runs the given pair once and returns the server's MirrorResult. The window for a COM
    // read is [now, now + settings.SyncWindowDays], matching the scheduler.
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
            var to = now.AddDays(settings.SyncWindowDays);
            var events = await comSource.ReadWindowAsync(now, to, ct);
            return await client.PushPairAsync(apiKey, pair.Id, events, ct);
        }

        return await client.RunPairAsync(apiKey, pair.Id, ct);
    }

    public static bool IsOutlookCom(SyncPair pair)
        => pair != null
           && string.Equals(pair.Source.Provider, OutlookComProvider, StringComparison.OrdinalIgnoreCase);
}
