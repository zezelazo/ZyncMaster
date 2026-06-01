using ZyncMaster.Core;

namespace ZyncMaster.Server;

// The calendar sync module: executes a single pair's window read + destructive mirror that
// today lives inline in PairEndpoints' /run. It resolves the source reader and destination
// writer for the pair (via ProviderRegistry) and returns the same MirrorResult the endpoint
// records and returns.
//
// Contract preserved from the inline /run:
//   - A pair whose SOURCE provider has no server reader (OutlookCom) yields a result with
//     NoServerReader = true so the endpoint can return the existing 409 "no_server_reader"
//     BEFORE acquiring the run-lock (events arrive via /push instead).
//   - A SOURCE read that fails transiently aborts BEFORE the destructive mirror and is
//     surfaced as Partial (Created/Updated/Deleted = 0, Partial = true, the read error in
//     Failures) so the caller retries later. Non-transient read errors propagate.
//   - The window sweep is conditional and lives in CalendarMirror; it is not duplicated here.
//
// The run-lock stays in the endpoint and WRAPS the call to ExecuteAsync.
public interface ICalendarSyncModule : ISyncModule
{
    Task<CalendarModuleResult> ExecuteAsync(
        SyncPair pair,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default);
}

// Outcome of a calendar module run. Either the source had no server reader (the endpoint maps
// this to 409 no_server_reader, exactly as the inline /run did), or Result carries the counts
// to record and return.
public sealed record CalendarModuleResult
{
    // True when the source provider has no server-side reader (OutlookCom). The endpoint
    // returns the existing 409 "no_server_reader" in this case and does NOT record a run.
    public bool NoServerReader { get; init; }

    // The mirror counts when a read+mirror actually ran (including the Partial shape when a
    // transient read failure aborted before the mirror). Null only when NoServerReader is true.
    public MirrorResult? Result { get; init; }
}
