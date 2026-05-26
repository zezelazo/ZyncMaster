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
    private readonly CompleteCalendarReader _reader;

    public JsonImportSource(IFileSystem fs)
    {
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
        _reader = new CompleteCalendarReader();
    }

    public ImportPayload Load(string path)
    {
        if (path == null) throw new ArgumentNullException(nameof(path));
        if (!_fs.FileExists(path))
            throw new ImportSourceException($"File not found: {path}");

        var raw = _fs.ReadAllText(path);

        // The Complete-JSON event shape (the events array + per-event mapping/validation)
        // is owned by SyncMaster.Core's CompleteCalendarReader so the engine and CalImport
        // stay DRY. CalImport layers its own header validation (period / exportedAt) on top.
        CalendarReadResult read;
        try
        {
            read = _reader.Parse(raw);
        }
        catch (CalendarReadException ex)
        {
            throw new ImportSourceException(ex.Message, ex.InnerException ?? ex);
        }

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

        var records = read.Events;
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

        // After per-occurrence keying, a collision means the same occurrence (same source
        // id AND same start) was listed more than once — a genuine data error.
        var sb = new StringBuilder("Duplicate occurrences found (same source id and start): ");
        for (int i = 0; i < duplicates.Count; i++)
        {
            if (i > 0) sb.Append("; ");
            var first = records[duplicates[i].Value[0]];
            sb.Append('\'').Append(first.Subject).Append("' starting ")
              .Append(first.StartOffset.ToString("o"))
              .Append(" appears ").Append(duplicates[i].Value.Count).Append(" times");
        }
        sb.Append(". Each occurrence must be unique.");
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
}
