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

        // Feature 2 — resolve which SOURCE calendars to read and MERGE into the single destination:
        //   * AllCalendars  => enumerate every calendar of the source account and read each;
        //   * CalendarIds   => read ONLY that subset;
        //   * neither (legacy) => read the single CalendarId.
        // The merge keys on AppointmentRecord.Id (the per-occurrence id), exactly like the COM path,
        // so the SAME event appearing in two source calendars collapses to one destination event and
        // two genuinely distinct events never collide. CalImportPairId == pair.Id keeps the sweep
        // scoped to this pair regardless of how many source calendars fed it.
        //
        // §A-3 GUARD (preserved across N reads): if ANY calendar read fails transiently (throttling,
        // 5xx, timeout, transport drop, truncated page) we abort BEFORE the destructive mirror and
        // report Partial. Feeding a short merged set into the sweep would delete events that still
        // exist at the source. Non-transient read errors (auth/consent, fatal contract) propagate.
        var writer = _registry.ResolveWriter(pair.Destination);

        IReadOnlyList<string> calendarIds;
        try
        {
            calendarIds = await ResolveSourceCalendarIdsAsync(reader, pair.Source, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (PairEndpoints.IsTransientReadFailure(ex))
        {
            // Enumerating the account's calendars (AllCalendars) can itself throttle; treat that as
            // a transient read so the run defers instead of mirroring an incomplete set.
            return new CalendarModuleResult { Result = PairEndpoints.PartialReadResult(ex) };
        }

        IReadOnlyList<AppointmentRecord> events;
        try
        {
            events = await ReadAndMergeAsync(reader, calendarIds, from, to, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (PairEndpoints.IsTransientReadFailure(ex))
        {
            return new CalendarModuleResult { Result = PairEndpoints.PartialReadResult(ex) };
        }

        var result = await writer
            .MirrorAsync(pair.Destination.CalendarId, events, ReminderMinutes, from, to, ct, pair.Id)
            .ConfigureAwait(false);

        return new CalendarModuleResult { Result = result };
    }

    // Resolves the set of SOURCE calendar ids to read (Feature 2). AllCalendars enumerates the
    // SOURCE account's calendars via the READER's ListCalendarsAsync — the reader is resolved
    // against pair.Source, so enumeration is anchored to the ORIGIN account (using the writer here
    // would enumerate the DESTINATION account whenever source and destination are distinct, reading
    // the wrong calendars). An explicit CalendarIds subset is used as-is (deduped, blanks dropped);
    // otherwise the legacy single CalendarId. Always returns at least one id when the source carries
    // a CalendarId, so a legacy pair behaves exactly as before.
    private static async Task<IReadOnlyList<string>> ResolveSourceCalendarIdsAsync(
        ICalendarReader reader, Endpoint source, CancellationToken ct)
    {
        if (source.AllCalendars)
        {
            var cals = await reader.ListCalendarsAsync(ct).ConfigureAwait(false);
            var all = cals
                .Select(c => c.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            // An account with no enumerable calendars falls back to the configured CalendarId (if any)
            // so the run reads SOMETHING rather than mirroring an empty set into the destructive sweep.
            if (all.Count == 0 && !string.IsNullOrWhiteSpace(source.CalendarId))
                all.Add(source.CalendarId);
            return all;
        }

        if (source.CalendarIds is { Count: > 0 })
            return source.CalendarIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();

        return new[] { source.CalendarId };
    }

    // Reads each source calendar's window and merges into one list, deduped by AppointmentRecord.Id
    // (keeping the first occurrence seen — same rule as OutlookComSource). A single calendar is the
    // common case and skips the dedupe set. Any transient read failure propagates so the caller's
    // §A-3 guard aborts before the mirror.
    private static async Task<IReadOnlyList<AppointmentRecord>> ReadAndMergeAsync(
        ICalendarReader reader,
        IReadOnlyList<string> calendarIds,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        if (calendarIds.Count == 1)
            return await reader.ReadWindowAsync(calendarIds[0], from, to, ct).ConfigureAwait(false);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<AppointmentRecord>();
        foreach (var calendarId in calendarIds)
        {
            ct.ThrowIfCancellationRequested();
            var events = await reader.ReadWindowAsync(calendarId, from, to, ct).ConfigureAwait(false);
            foreach (var ev in events)
            {
                if (seen.Add(ev.Id))
                    merged.Add(ev);
            }
        }
        return merged;
    }
}
