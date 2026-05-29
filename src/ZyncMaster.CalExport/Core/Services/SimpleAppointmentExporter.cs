using System.Collections.Generic;
using System.Linq;
using ZyncMaster.Core;

namespace ZyncMaster.CalExport;

public sealed class SimpleAppointmentExporter : IAppointmentExporter
{
    public string FileSuffix    => "simple";
    public string FileExtension => "txt";

    public string Serialize(IReadOnlyList<AppointmentRecord> records, ExportContext context)
    {
        if (records == null || records.Count == 0)
            return "";

        return string.Join("\n", records.Select(ToLine));
    }

    private static string ToLine(AppointmentRecord r)
    {
        var date = r.Start.ToString("yyyy-MM-dd");

        string time, dur;
        if (r.IsAllDay)
        {
            time = "All day";
            dur  = "All day";
        }
        else
        {
            time = r.Start.ToString("HH:mm");
            dur  = $"{r.Duration / 60}h {r.Duration % 60:D2}m";
        }

        var creator = r.OrganizerEmail.Length > 0
            ? $"{r.OrganizerName} <{r.OrganizerEmail}>"
            : r.OrganizerName;

        var line = $"{date} | {time} | {dur} | {r.Subject} | {creator}";
        return r.IsCancelled ? $"{line} | CANCELADO" : line;
    }
}
