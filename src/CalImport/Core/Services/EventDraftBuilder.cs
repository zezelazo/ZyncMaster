using System;
using SyncMaster.Core;

namespace SyncMaster.CalImport;

public sealed class EventDraftBuilder
{
    private readonly IParticipantRenderer _renderer;

    public EventDraftBuilder(IParticipantRenderer renderer)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
    }

    public EventDraft BuildForCreate(AppointmentRecord record, int reminderMinutes)
    {
        if (record == null) throw new ArgumentNullException(nameof(record));

        var body = _renderer.BuildBodyForCreate(record.Description, record.Participants);
        return BuildCommon(record, reminderMinutes, body);
    }

    public EventDraft BuildForUpdate(AppointmentRecord record, int reminderMinutes, string existingBodyHtml)
    {
        if (record == null) throw new ArgumentNullException(nameof(record));

        var body = _renderer.MergeIntoExistingBody(existingBodyHtml ?? "", record.Participants);
        return BuildCommon(record, reminderMinutes, body);
    }

    private static EventDraft BuildCommon(AppointmentRecord record, int reminderMinutes, string body)
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
        };
    }
}
