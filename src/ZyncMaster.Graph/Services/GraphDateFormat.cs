using System;
using System.Globalization;

namespace ZyncMaster.Graph;

// Formats a start/end pair for the Graph event payload. Same three branches the pair mirror
// uses (all-day uses the LOCAL date so the day boundary never shifts; a "UTC" zone label is
// coerced to real UTC instants so datetime and timeZone agree; otherwise local wall-clock +
// the original zone id).
public static class GraphDateFormat
{
    public static (string Start, string End, string TimeZone) For(
        DateTimeOffset start, DateTimeOffset end, string timeZoneId, bool isAllDay)
    {
        if (isAllDay)
        {
            var startDate = start.Date;
            var endDate = end.Date;
            if (endDate <= startDate)
                endDate = startDate.AddDays(1);
            return (
                startDate.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture),
                endDate.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture),
                "UTC");
        }

        if (string.Equals(timeZoneId, "UTC", StringComparison.Ordinal))
        {
            return (
                start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture),
                end.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture),
                "UTC");
        }

        return (
            start.DateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            end.DateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            timeZoneId);
    }
}
