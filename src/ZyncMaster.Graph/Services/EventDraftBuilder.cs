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

        var body = _renderer.MergeIntoExistingBody(existingBodyHtml ?? "", record.Participants);
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
