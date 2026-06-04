using System.Collections.Generic;
using System.Linq;

namespace ZyncMaster.Core;

// Single source of truth for the Simple-mode pipe-delimited line format. Both the
// CalExport COM exporter (SimpleAppointmentExporter) and the Server's Graph-source
// .txt export delegate here, so a record produced from Outlook COM and one read from
// Microsoft Graph serialize to a byte-identical line. Columns, in order:
//   date | time | dur | subject | creator [| CANCELADO]
// separated by " | " (space-pipe-space); records joined by "\n"; "" when empty.
public static class SimpleAppointmentFormatter
{
    public static string Format(IReadOnlyList<AppointmentRecord> records)
    {
        if (records == null || records.Count == 0)
            return "";

        return string.Join("\n", records.Select(FormatLine));
    }

    public static string FormatLine(AppointmentRecord r)
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
