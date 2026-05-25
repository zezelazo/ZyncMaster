using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SyncMaster.Core;

namespace SyncMaster.CalImport;

public sealed class JsonImportSource : IImportSource
{
    private readonly IFileSystem _fs;

    public JsonImportSource(IFileSystem fs)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
    }

    public ImportPayload Load(string path)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));
        if (!_fs.FileExists(path))
            throw new ImportSourceException($"File not found: {path}");

        var raw = _fs.ReadAllText(path);

        JObject root;
        try
        {
            using var sr = new System.IO.StringReader(raw);
            using var jr = new JsonTextReader(sr) { DateParseHandling = DateParseHandling.None };
            root = JObject.Load(jr);
        }
        catch (JsonException ex)
        {
            throw new ImportSourceException($"File is not valid JSON: {path}", ex);
        }

        if (root["events"] is not JArray events)
            throw new ImportSourceException("JSON is missing 'events' array. Was the file produced by CalExport Complete mode?");

        var exportedAt = ParseExportedAt(root);

        if (root["period"] is not JObject period)
            throw new ImportSourceException("JSON is missing required 'period' object.");

        var year      = ReadRequiredPeriodInt(period, "year");
        var month     = ReadRequiredPeriodInt(period, "month");
        var monthName = period["monthName"]?.Value<string>() ?? "";

        if (year < 1)
            throw new ImportSourceException($"period.year must be >= 1, got {year}.");
        if (month < 1 || month > 12)
            throw new ImportSourceException($"period.month must be between 1 and 12, got {month}.");

        var calendarsArr = root["calendars"] as JArray;
        var calendars    = new List<string>();
        if (calendarsArr != null)
            foreach (var c in calendarsArr)
                calendars.Add(c.Value<string>() ?? "");

        var records = new List<AppointmentRecord>(events.Count);
        for (int i = 0; i < events.Count; i++)
        {
            var ev = events[i] as JObject
                     ?? throw new ImportSourceException($"events[{i}] is not an object.");

            if (ev["id"] == null)
                throw new ImportSourceException(
                    $"events[{i}] is missing 'id'. This file was produced by an older CalExport. " +
                    "Re-export with the current version.");

            var id = ev["id"]?.Value<string>() ?? "";
            if (string.IsNullOrEmpty(id))
                throw new ImportSourceException(
                    $"events[{i}] has empty 'id'. Each event must have a non-empty unique id.");

            records.Add(ToRecord(ev, i));
        }

        EnsureUniqueIds(records);

        return new ImportPayload
        {
            ExportedAt = exportedAt,
            Year       = year,
            Month      = month,
            MonthName  = monthName,
            Calendars  = calendars,
            Events     = records,
        };
    }

    private static void EnsureUniqueIds(IReadOnlyList<AppointmentRecord> records)
    {
        var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < records.Count; i++)
        {
            var id = records[i].Id;
            if (!groups.TryGetValue(id, out var list))
            {
                list = new List<int>();
                groups[id] = list;
            }
            list.Add(i);
        }

        var duplicates = groups.Where(kv => kv.Value.Count > 1).ToList();
        if (duplicates.Count == 0) return;

        var sb = new StringBuilder("Duplicate event ids found in payload: ");
        for (int i = 0; i < duplicates.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append('\'').Append(duplicates[i].Key).Append("' at indexes [")
              .Append(string.Join(",", duplicates[i].Value))
              .Append(']');
        }
        sb.Append(". Each event must have a unique id.");
        throw new ImportSourceException(sb.ToString());
    }

    private static int ReadRequiredPeriodInt(JObject period, string fieldName)
    {
        var token = period[fieldName];
        if (token == null || token.Type == JTokenType.Null)
            throw new ImportSourceException($"Required field 'period.{fieldName}' is missing.");
        try
        {
            return token.Value<int>();
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or JsonException)
        {
            throw new ImportSourceException($"Required field 'period.{fieldName}' is not a valid integer.", ex);
        }
    }

    private static DateTimeOffset ParseExportedAt(JObject root)
    {
        var token = root["exportedAt"];
        if (token == null || token.Type == JTokenType.Null)
            throw new ImportSourceException("Required field 'exportedAt' is missing.");

        var s = token.Value<string>();
        if (string.IsNullOrEmpty(s))
            throw new ImportSourceException("Required field 'exportedAt' is empty.");

        if (!DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var v))
            throw new ImportSourceException($"Field 'exportedAt' is not a valid ISO 8601 timestamp: '{s}'.");

        return v;
    }

    private static AppointmentRecord ToRecord(JObject ev, int index)
    {
        RequireProperty(ev, "subject", index);

        var id              = ev["id"]?.Value<string>()                       ?? "";
        var subject         = ev["subject"]?.Value<string>()                  ?? "";
        var isAllDay        = ReadRequiredBool(ev, "isAllDay", index);
        var isCancelled     = ev["isCancelled"]?.Value<bool>()                ?? false;
        var startStr        = ev["start"]?.Value<string>()                    ?? "";
        var endStr          = ev["end"]?.Value<string>()                      ?? "";
        var tzId            = ev["startTimeZoneId"]?.Value<string>()          ?? "";
        var tzDisplay       = ev["startTimeZoneDisplayName"]?.Value<string>() ?? "";
        var durationMinutes = ReadRequiredInt(ev, "durationMinutes", index);
        var description     = ev["description"]?.Value<string>()              ?? "";

        var organizer       = ev["organizer"] as JObject;
        var organizerName   = organizer?["name"]?.Value<string>()  ?? "";
        var organizerEmail  = organizer?["email"]?.Value<string>() ?? "";

        var startOffset = ParseOffset(startStr, $"events[{index}].start");
        var endOffset   = ParseOffset(endStr,   $"events[{index}].end");

        var participantsArr = ev["participants"] as JArray;
        var participants    = new List<ParticipantRecord>();
        if (participantsArr != null)
        {
            foreach (var p in participantsArr)
            {
                if (p is not JObject po) continue;
                participants.Add(new ParticipantRecord
                {
                    Name     = po["name"]?.Value<string>()     ?? "",
                    Email    = po["email"]?.Value<string>()    ?? "",
                    Type     = po["type"]?.Value<string>()     ?? "",
                    Response = po["response"]?.Value<string>() ?? "",
                });
            }
        }

        return new AppointmentRecord
        {
            Id                       = id,
            Start                    = startOffset.LocalDateTime,
            Duration                 = durationMinutes,
            IsAllDay                 = isAllDay,
            Subject                  = subject,
            OrganizerName            = organizerName,
            OrganizerEmail           = organizerEmail,
            IsCancelled              = isCancelled,
            Description              = description,
            StartOffset              = startOffset,
            EndOffset                = endOffset,
            StartTimeZoneId          = tzId,
            StartTimeZoneDisplayName = tzDisplay,
            Participants             = participants,
        };
    }

    private static void RequireProperty(JObject ev, string name, int index)
    {
        var token = ev[name];
        if (token == null || token.Type == JTokenType.Null)
            throw new ImportSourceException(
                $"events[{index}] is missing required field '{name}'.");
    }

    private static int ReadRequiredInt(JObject ev, string name, int index)
    {
        var token = ev[name];
        if (token == null || token.Type == JTokenType.Null)
            throw new ImportSourceException(
                $"events[{index}] is missing required field '{name}'.");

        try
        {
            return token.Value<int>();
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or JsonException)
        {
            throw new ImportSourceException(
                $"events[{index}].{name} is not a valid integer.", ex);
        }
    }

    private static bool ReadRequiredBool(JObject ev, string name, int index)
    {
        var token = ev[name];
        if (token == null || token.Type == JTokenType.Null)
            throw new ImportSourceException(
                $"events[{index}] is missing required field '{name}'.");

        try
        {
            return token.Value<bool>();
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or JsonException)
        {
            throw new ImportSourceException(
                $"events[{index}].{name} is not a valid boolean.", ex);
        }
    }

    private static DateTimeOffset ParseOffset(string value, string fieldName)
    {
        if (string.IsNullOrEmpty(value))
            throw new ImportSourceException($"{fieldName} is missing or empty.");
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var v))
            throw new ImportSourceException($"{fieldName} is not a valid ISO 8601 timestamp: '{value}'.");
        return v;
    }
}
