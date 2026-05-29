using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ZyncMaster.Core;

namespace ZyncMaster.Graph;

// Orchestrates a one-way mirror of a set of appointment records onto a Graph calendar:
// upserts every record (create / update / delete-on-cancel via the import plan), then
// sweeps the [fromUtc, toUtc] window and deletes managed events that no longer appear in
// the payload. Each Graph call is wrapped per-item so a single failure does not abort the run.
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
        var failures = new List<string>();

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
                failures.Add($"{item.Action} failed for source id '{item.Record.Id}': {ex.Message}");
            }
        }

        // Window sweep: anything managed by this tool in the window that is not represented
        // by a live (non-cancelled) record in the payload is an orphan and gets deleted.
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
                failures.Add($"Window delete failed for source id '{managedRef.SourceId}' (event '{managedRef.EventId}'): {ex.Message}");
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
