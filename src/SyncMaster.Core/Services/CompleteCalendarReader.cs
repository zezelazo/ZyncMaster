using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SyncMaster.Core;

// Parses CalExport's Complete-mode JSON into AppointmentRecords.
//
// This is the single source of truth for reading the Complete-JSON event shape.
// Both CalImport (JsonImportSource) and the sync engine delegate here so the
// mapping + validation rules stay in one place.
public sealed class CompleteCalendarReader
{
    public CalendarReadResult Parse(string json)
    {
        if (json == null) throw new ArgumentNullException(nameof(json));

        JObject root;
        try
        {
            using var sr = new StringReader(json);
            using var jr = new JsonTextReader(sr) { DateParseHandling = DateParseHandling.None };
            root = JObject.Load(jr);
        }
        catch (JsonException ex)
        {
            throw new CalendarReadException("File is not valid JSON.", ex);
        }

        if (root["events"] is not JArray events)
            throw new CalendarReadException(
                "JSON is missing 'events' array. Was the file produced by CalExport Complete mode?");

        var records = new List<AppointmentRecord>(events.Count);
        for (int i = 0; i < events.Count; i++)
        {
            var ev = events[i] as JObject
                     ?? throw new CalendarReadException($"events[{i}] is not an object.");

            if (ev["id"] == null)
                throw new CalendarReadException(
                    $"events[{i}] is missing 'id'. This file was produced by an older CalExport. " +
                    "Re-export with the current version.");

            var id = ev["id"]?.Value<string>() ?? "";
            if (string.IsNullOrEmpty(id))
                throw new CalendarReadException(
                    $"events[{i}] has empty 'id'. Each event must have a non-empty unique id.");

            records.Add(ToRecord(ev, i));
        }

        return new CalendarReadResult
        {
            Events = records,
            PeriodLabel = BuildPeriodLabel(root),
        };
    }

    private static string? BuildPeriodLabel(JObject root)
    {
        if (root["period"] is not JObject period)
            return null;

        var monthName = period["monthName"]?.Value<string>();
        var yearToken = period["year"];
        var year = yearToken != null && yearToken.Type != JTokenType.Null
            ? yearToken.Value<int?>()
            : null;

        if (!string.IsNullOrEmpty(monthName) && year.HasValue)
            return $"{monthName} {year.Value}";
        if (!string.IsNullOrEmpty(monthName))
            return monthName;
        if (year.HasValue)
            return year.Value.ToString(CultureInfo.InvariantCulture);
        return null;
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
            // Per-occurrence key: the raw id (a recurring series shares one across all
            // occurrences) combined with the occurrence start, so each occurrence upserts
            // as its own event while staying idempotent across runs.
            Id                       = OccurrenceId.For(id, startOffset),
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
            throw new CalendarReadException(
                $"events[{index}] is missing required field '{name}'.");
    }

    private static int ReadRequiredInt(JObject ev, string name, int index)
    {
        var token = ev[name];
        if (token == null || token.Type == JTokenType.Null)
            throw new CalendarReadException(
                $"events[{index}] is missing required field '{name}'.");

        try
        {
            return token.Value<int>();
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or JsonException)
        {
            throw new CalendarReadException(
                $"events[{index}].{name} is not a valid integer.", ex);
        }
    }

    private static bool ReadRequiredBool(JObject ev, string name, int index)
    {
        var token = ev[name];
        if (token == null || token.Type == JTokenType.Null)
            throw new CalendarReadException(
                $"events[{index}] is missing required field '{name}'.");

        try
        {
            return token.Value<bool>();
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or JsonException)
        {
            throw new CalendarReadException(
                $"events[{index}].{name} is not a valid boolean.", ex);
        }
    }

    private static DateTimeOffset ParseOffset(string value, string fieldName)
    {
        if (string.IsNullOrEmpty(value))
            throw new CalendarReadException($"{fieldName} is missing or empty.");
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var v))
            throw new CalendarReadException($"{fieldName} is not a valid ISO 8601 timestamp: '{value}'.");
        return v;
    }
}
