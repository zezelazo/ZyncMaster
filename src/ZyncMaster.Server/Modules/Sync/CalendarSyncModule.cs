using ZyncMaster.Core;
using ZyncMaster.Graph;

namespace ZyncMaster.Server;

// The calendar module. Owns the read + destructive mirror for a single pair that previously
// lived inline in PairEndpoints' /run: resolve the source reader and destination writer for
// the pair via ProviderRegistry, read the window (guarded so a transient read failure becomes
// a Partial instead of feeding a short set into the destructive sweep), call MirrorAsync, and
// return the counts. The conditional window sweep stays in CalendarMirror (not duplicated).
//
// What is intentionally NOT here:
//   - The per-pair run-lock — it stays in the endpoint and wraps ExecuteAsync.
//   - The pair lookup / 404 — the endpoint loads the pair before calling the module.
public sealed class CalendarSyncModule : ICalendarSyncModule
{
    public const string Id = "calendar";

    private readonly ProviderRegistry _registry;

    public CalendarSyncModule(ProviderRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public string ModuleId => Id;

    private const int ReminderMinutes = 30;

    public async Task<CalendarModuleResult> ExecuteAsync(
        SyncPair pair,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pair);

        var reader = _registry.ResolveReader(pair.Source);
        if (reader is null)
        {
            // OutlookCom sources have no server-side read; their events arrive via /push.
            // The endpoint maps this to the existing 409 "no_server_reader".
            return new CalendarModuleResult { NoServerReader = true };
        }

        // §A-3 — a SOURCE read that fails transiently (throttling, 5xx, timeout, transport
        // drop, or a truncated/malformed paged read) must NOT flow into the destructive
        // mirror: a short source set would make the sweep delete events that still exist at
        // the source. Abort before the mirror and report Partial (the same "retry me later"
        // contract as a partial upsert) instead of a generic 500. Non-transient read errors
        // (auth/consent, fatal contract) still propagate to the global handler.
        IReadOnlyList<AppointmentRecord> events;
        try
        {
            events = await reader.ReadWindowAsync(pair.Source.CalendarId, from, to, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (PairEndpoints.IsTransientReadFailure(ex))
        {
            return new CalendarModuleResult { Result = PairEndpoints.PartialReadResult(ex) };
        }

        var writer = _registry.ResolveWriter(pair.Destination);
        var result = await writer
            .MirrorAsync(pair.Destination.CalendarId, events, ReminderMinutes, from, to, ct)
            .ConfigureAwait(false);

        return new CalendarModuleResult { Result = result };
    }
}
