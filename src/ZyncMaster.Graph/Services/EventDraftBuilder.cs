using System;
using ZyncMaster.Core;

namespace ZyncMaster.Graph;

public sealed class EventDraftBuilder
{
    private readonly IParticipantRenderer _renderer;

    public EventDraftBuilder(IParticipantRenderer renderer)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
    }

    public EventDraft BuildForCreate(AppointmentRecord record, int reminderMinutes, string pairId = "")
    {
        if (record == null) throw new ArgumentNullException(nameof(record));

        var body = _renderer.BuildBodyForCreate(record.Description, record.Participants);
        return BuildCommon(record, reminderMinutes, body, pairId);
    }

    public EventDraft BuildForUpdate(AppointmentRecord record, int reminderMinutes, string existingBodyHtml, string pairId = "")
    {
        if (record == null) throw new ArgumentNullException(nameof(record));

        // The destination event is a managed MIRROR of the source: subject/start/end are overwritten on
        // every update, so the body is REPLACED with a fresh render of the source too — NOT merged into
        // the destination's current body. The old merge relied on HTML-comment markers
        // (<!-- calimport:participants... -->) to find and replace the prior participants block, but
        // Microsoft Graph STRIPS HTML comments when it saves an event body. So on the next sync the markers
        // were gone, the merge couldn't find the block, and it PREPENDED a fresh one every time — the
        // participants list accumulated N copies. Rebuilding from the source each update is idempotent and
        // replaces completely. existingBodyHtml is intentionally no longer used.
        var body = _renderer.BuildBodyForCreate(record.Description, record.Participants);
        return BuildCommon(record, reminderMinutes, body, pairId);
    }

    private static EventDraft BuildCommon(AppointmentRecord record, int reminderMinutes, string body, string pairId)
    {
        var tz = string.IsNullOrWhiteSpace(record.StartTimeZoneId) ? "UTC" : record.StartTimeZoneId;

        return new EventDraft
        {
            Subject                    = record.Subject,
            BodyHtml                   = body,
            Start                      = record.StartOffset,
            End                        = record.EndOffset,
            TimeZoneId                 = tz,
            IsAllDay                   = record.IsAllDay,
            ReminderMinutesBeforeStart = reminderMinutes,
            ExternalId                 = record.Id,
            PairId                     = pairId ?? "",
        };
    }
}
