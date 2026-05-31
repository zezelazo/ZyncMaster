using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ZyncMaster.Core;

namespace ZyncMaster.Graph;

// Orchestrates a one-way mirror of a set of appointment records onto a Graph calendar:
// upserts every record (create / update / delete-on-cancel via the import plan), then
// CONDITIONALLY sweeps the [fromUtc, toUtc] window and deletes managed events that no
// longer appear in the payload.
//
// Data-loss guard (plan v2 §B-2): the sweep is destructive — it deletes any managed event
// not present in the payload. If a transient failure (429 / timeout / network drop) hit the
// upsert, the payload we managed to apply may be incomplete, so sweeping would delete the
// user's legitimate events. In that case we SKIP the sweep entirely and return Partial=true
// so the caller retries later. We only sweep when the payload was applied with no transient
// failures, i.e. we know the payload is complete.
public sealed class CalendarMirror
{
    private readonly ICalendarTarget   _target;
    private readonly ImportPlanBuilder _planBuilder;
    private readonly EventDraftBuilder _draftBuilder;

    public CalendarMirror(ICalendarTarget target, ImportPlanBuilder planBuilder, EventDraftBuilder draftBuilder)
    {
        _target       = target       ?? throw new ArgumentNullException(nameof(target));
        _planBuilder  = planBuilder  ?? throw new ArgumentNullException(nameof(planBuilder));
        _draftBuilder = draftBuilder ?? throw new ArgumentNullException(nameof(draftBuilder));
    }

    public async Task<MirrorOutcome> MirrorAsync(
        string                           calendarId,
        IReadOnlyList<AppointmentRecord> records,
        int                              reminderMinutes,
        DateTimeOffset                   fromUtc,
        DateTimeOffset                   toUtc,
        CancellationToken                ct = default)
    {
        if (string.IsNullOrWhiteSpace(calendarId)) throw new ArgumentException("calendarId required.", nameof(calendarId));
        if (records == null) throw new ArgumentNullException(nameof(records));

        var created  = 0;
        var updated  = 0;
        var deleted  = 0;
        var skipped  = 0;
        var failures = new List<MirrorFailure>();

        var existing = await _target
            .FindByExternalIdsAsync(calendarId, records.Select(r => r.Id).ToList(), ct)
            .ConfigureAwait(false);

        var plan = _planBuilder.Build(records, existing);

        foreach (var item in plan)
        {
            try
            {
                switch (item.Action)
                {
                    case ImportAction.Create:
                        await _target.CreateEventAsync(
                            calendarId,
                            _draftBuilder.BuildForCreate(item.Record, reminderMinutes),
                            ct).ConfigureAwait(false);
                        created++;
                        break;

                    case ImportAction.Update:
                        await _target.UpdateEventAsync(
                            item.ExistingEventId!,
                            _draftBuilder.BuildForUpdate(item.Record, reminderMinutes, item.ExistingBodyHtml ?? ""),
                            ct).ConfigureAwait(false);
                        updated++;
                        break;

                    case ImportAction.Cancel:
                        await _target.DeleteEventAsync(item.ExistingEventId!, ct).ConfigureAwait(false);
                        deleted++;
                        break;

                    case ImportAction.Skip:
                        skipped++;
                        break;
                }
            }
            catch (Exception ex)
            {
                failures.Add(new MirrorFailure
                {
                    Kind    = SyncErrorClassifier.Classify(ex),
                    Message = $"{item.Action} failed for source id '{item.Record.Id}': {ex.Message}",
                });
            }
        }

        // §B-2 — conditional sweep. A transient failure means the applied payload may be
        // incomplete; deleting "orphans" now could wipe legitimate events that simply did
        // not get applied this run. Skip the destructive sweep and report Partial so the
        // caller retries. The non-destructive upsert work above is preserved.
        var hadTransient = failures.Any(f => f.Kind == SyncErrorKind.Transient);
        if (hadTransient)
        {
            return new MirrorOutcome
            {
                Created  = created,
                Updated  = updated,
                Deleted  = deleted,
                Skipped  = skipped,
                Failures = failures,
                Partial  = true,
            };
        }

        // Window sweep: anything managed by this tool in the window that is not represented
        // by a live (non-cancelled) record in the payload is an orphan and gets deleted.
        // Safe to run only because we got here with no transient failures.
        var liveIds = new HashSet<string>(
            records.Where(r => !r.IsCancelled).Select(r => r.Id),
            StringComparer.Ordinal);

        var managed = await _target
            .ListManagedInWindowAsync(calendarId, fromUtc, toUtc, ct)
            .ConfigureAwait(false);

        foreach (var managedRef in managed)
        {
            if (liveIds.Contains(managedRef.SourceId))
                continue;

            try
            {
                await _target.DeleteEventAsync(managedRef.EventId, ct).ConfigureAwait(false);
                deleted++;
            }
            catch (Exception ex)
            {
                failures.Add(new MirrorFailure
                {
                    Kind    = SyncErrorClassifier.Classify(ex),
                    Message = $"Window delete failed for source id '{managedRef.SourceId}' (event '{managedRef.EventId}'): {ex.Message}",
                });
            }
        }

        return new MirrorOutcome
        {
            Created  = created,
            Updated  = updated,
            Deleted  = deleted,
            Skipped  = skipped,
            Failures = failures,
        };
    }
}
