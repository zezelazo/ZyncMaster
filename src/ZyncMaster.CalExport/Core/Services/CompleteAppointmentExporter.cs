using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using ZyncMaster.Core;

namespace ZyncMaster.CalExport;

public sealed class CompleteAppointmentExporter : IAppointmentExporter
{
    public string FileSuffix    => "complete";
    public string FileExtension => "json";

    public string Serialize(IReadOnlyList<AppointmentRecord> records, ExportContext context)
    {
        var exportObj = new
        {
            exportedAt = context.ExportedAt.ToString("o"),
            period = new
            {
                year      = context.Year,
                month     = context.Month,
                monthName = context.MonthName,
            },
            calendars = context.CalendarDisplayNames.Count == 1 && context.CalendarDisplayNames[0] == "all"
                ? (object)new[] { "all" }
                : context.CalendarDisplayNames.ToArray(),
            events = records.Select(r => new
            {
                id                       = r.Id,
                subject                  = r.Subject,
                isAllDay                 = r.IsAllDay,
                isCancelled              = r.IsCancelled,
                start                    = r.StartOffset.ToString("o"),
                startUtc                 = r.StartOffset.UtcDateTime.ToString("o") + "Z",
                startTimeZoneId          = r.StartTimeZoneId,
                startTimeZoneDisplayName = r.StartTimeZoneDisplayName,
                end                      = r.EndOffset.ToString("o"),
                endUtc                   = r.EndOffset.UtcDateTime.ToString("o") + "Z",
                durationMinutes          = r.Duration,
                organizer = new
                {
                    name  = r.OrganizerName,
                    email = r.OrganizerEmail,
                },
                description  = r.Description,
                participants = r.Participants.Select(p => new
                {
                    name     = p.Name,
                    email    = p.Email,
                    type     = p.Type,
                    response = p.Response,
                }).ToList(),
            }).ToList(),
        };

        return JsonConvert.SerializeObject(exportObj, Formatting.Indented);
    }
}
