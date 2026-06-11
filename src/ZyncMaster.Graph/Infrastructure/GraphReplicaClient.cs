using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ZyncMaster.Core;

namespace ZyncMaster.Graph;

// Graph implementation of the replica engine client. Thin wrapper over GraphJsonHttp (the
// shared transport): the testable JSON shapes are exercised by GraphReplicaClientWireTests
// with a capturing handler — the privacy whitelist is asserted ON THE WIRE there.
//
// Property identifiers:
//   ZmReplicaOf      — String {ReplicaGuid} Name ZmReplicaOf      (replica mark, opaque uuid)
//   ZmRuleProcessed  — String {ReplicaGuid} Name ZmRuleProcessed  (rule fired-once stamp)
//   CalImportSourceId— String {CalImportGuid} Name CalImportSourceId (read-only here: the pair
//                      mirror's mark, consulted ONLY to keep its events out of this engine)
public sealed class GraphReplicaClient : IReplicaGraphClient
{
    private readonly GraphJsonHttp _io;
    private readonly string _replicaPropId;
    private readonly string _rulePropId;
    private readonly string _calImportPropId;

    public GraphReplicaClient(
        HttpClient http, IGraphTokenProvider auth, Guid replicaPropertyGuid, Guid calImportPropertyGuid)
    {
        _io = new GraphJsonHttp(http, auth);
        if (replicaPropertyGuid == calImportPropertyGuid)
            throw new ArgumentException(
                "The replica property GUID must differ from CalImport's — the engine separation " +
                "(spec §7) relies on the two never colliding.", nameof(replicaPropertyGuid));

        var rg = replicaPropertyGuid.ToString("D").ToUpperInvariant();
        var cg = calImportPropertyGuid.ToString("D").ToUpperInvariant();
        _replicaPropId = $"String {{{rg}}} Name ZmReplicaOf";
        _rulePropId = $"String {{{rg}}} Name ZmRuleProcessed";
        _calImportPropId = $"String {{{cg}}} Name CalImportSourceId";
    }

    public async Task<IReadOnlyList<CalendarTargetInfo>> ListCalendarsAsync(CancellationToken ct = default)
    {
        var json = await _io.SendJsonAsync(HttpMethod.Get,
            "me/calendars?$select=id,name,isDefaultCalendar,owner", null, ct).ConfigureAwait(false);
        var list = new List<CalendarTargetInfo>();
        foreach (var item in GraphJsonHttp.RequireCollection(json, "me/calendars"))
        {
            list.Add(new CalendarTargetInfo
            {
                Id = item["id"]?.Value<string>() ?? "",
                DisplayName = item["name"]?.Value<string>() ?? "",
                IsDefault = item["isDefaultCalendar"]?.Value<bool>() ?? false,
                Owner = item["owner"]?["address"]?.Value<string>() ?? "",
            });
        }
        return list;
    }

    public async Task<SourceEventSnapshot?> GetEventAsync(string eventId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(eventId)) throw new ArgumentException("eventId required.", nameof(eventId));

        var url =
            $"me/events/{Uri.EscapeDataString(eventId)}" +
            $"?$select=id,iCalUId,subject,start,end,isAllDay,isCancelled,showAs,isOrganizer,attendees" +
            $"&$expand={Uri.EscapeDataString(ExpandMarks())}";

        var json = await _io.TrySendJsonAsync(HttpMethod.Get, url, null, ct, preferUtcTimezone: true)
            .ConfigureAwait(false);
        return json is null ? null : MapSnapshot(json);
    }

    public async Task<IReadOnlyList<SourceEventSnapshot>> ListWindowAsync(
        string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(calendarId)) throw new ArgumentException("calendarId required.", nameof(calendarId));

        var start = fromUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var end = toUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var url =
            $"me/calendars/{Uri.EscapeDataString(calendarId)}/calendarView" +
            $"?startDateTime={Uri.EscapeDataString(start)}" +
            $"&endDateTime={Uri.EscapeDataString(end)}" +
            $"&$select=id,iCalUId,subject,start,end,isAllDay,isCancelled,showAs,isOrganizer,attendees" +
            $"&$expand={Uri.EscapeDataString(ExpandMarks())}" +
            $"&$top=50";

        var result = new List<SourceEventSnapshot>();
        while (!string.IsNullOrEmpty(url))
        {
            var json = await _io.SendJsonAsync(HttpMethod.Get, url, null, ct, preferUtcTimezone: true)
                .ConfigureAwait(false);
            foreach (var ev in GraphJsonHttp.RequireCollection(json, url))
            {
                var snap = MapSnapshot(ev);
                if (snap.GraphEventId.Length > 0)
                    result.Add(snap);
            }
            url = json["@odata.nextLink"]?.Value<string>() ?? "";
        }
        return result;
    }

    public async Task<string> CreateReplicaAsync(string calendarId, ReplicaDraft draft, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(calendarId)) throw new ArgumentException("calendarId required.", nameof(calendarId));
        ArgumentNullException.ThrowIfNull(draft);

        var (s, e, tz) = GraphDateFormat.For(draft.Start, draft.End, draft.TimeZoneId, draft.IsAllDay);

        // THE whitelist payload (spec §3/§12): subject(=mask), start, end, isAllDay, showAs and
        // the opaque ZmReplicaOf mark. NO body, NO attendees (zero invitations), NO location,
        // NO reminder (destination calendar default applies). The wire tests freeze this set.
        var json = new JObject
        {
            ["subject"] = draft.MaskTitle,
            ["start"] = new JObject { ["dateTime"] = s, ["timeZone"] = tz },
            ["end"] = new JObject { ["dateTime"] = e, ["timeZone"] = tz },
            ["isAllDay"] = draft.IsAllDay,
            ["showAs"] = draft.ShowAs,
            ["singleValueExtendedProperties"] = new JArray
            {
                new JObject { ["id"] = _replicaPropId, ["value"] = draft.SourceEventId },
            },
        };

        var response = await _io.SendJsonAsync(HttpMethod.Post,
            $"me/calendars/{Uri.EscapeDataString(calendarId)}/events",
            json.ToString(Formatting.None), ct).ConfigureAwait(false);
        return response["id"]?.Value<string>() ?? "";
    }

    public async Task UpdateReplicaTimesAsync(string eventId, ReplicaDraft draft, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(eventId)) throw new ArgumentException("eventId required.", nameof(eventId));
        ArgumentNullException.ThrowIfNull(draft);

        var (s, e, tz) = GraphDateFormat.For(draft.Start, draft.End, draft.TimeZoneId, draft.IsAllDay);

        // Propagation NEVER touches the subject: the mask title belongs to the user (spec §3).
        var json = new JObject
        {
            ["start"] = new JObject { ["dateTime"] = s, ["timeZone"] = tz },
            ["end"] = new JObject { ["dateTime"] = e, ["timeZone"] = tz },
            ["isAllDay"] = draft.IsAllDay,
            ["showAs"] = draft.ShowAs,
        };
        await _io.SendJsonAsync(new HttpMethod("PATCH"),
            $"me/events/{Uri.EscapeDataString(eventId)}", json.ToString(Formatting.None), ct)
            .ConfigureAwait(false);
    }

    public async Task UpdateSubjectAsync(string eventId, string subject, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(eventId)) throw new ArgumentException("eventId required.", nameof(eventId));
        ArgumentNullException.ThrowIfNull(subject);

        var json = new JObject { ["subject"] = subject };
        await _io.SendJsonAsync(new HttpMethod("PATCH"),
            $"me/events/{Uri.EscapeDataString(eventId)}", json.ToString(Formatting.None), ct)
            .ConfigureAwait(false);
    }

    public async Task StampRuleProcessedAsync(string eventId, string ruleId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(eventId)) throw new ArgumentException("eventId required.", nameof(eventId));
        if (string.IsNullOrWhiteSpace(ruleId)) throw new ArgumentException("ruleId required.", nameof(ruleId));

        var json = new JObject
        {
            ["singleValueExtendedProperties"] = new JArray
            {
                new JObject { ["id"] = _rulePropId, ["value"] = ruleId },
            },
        };
        await _io.SendJsonAsync(new HttpMethod("PATCH"),
            $"me/events/{Uri.EscapeDataString(eventId)}", json.ToString(Formatting.None), ct)
            .ConfigureAwait(false);
    }

    public async Task DeleteEventAsync(string eventId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(eventId)) throw new ArgumentException("eventId required.", nameof(eventId));

        // TrySend: a 404 means the replica is already gone — exactly the desired end state.
        await _io.TrySendJsonAsync(HttpMethod.Delete,
            $"me/events/{Uri.EscapeDataString(eventId)}", null, ct).ConfigureAwait(false);
    }

    public async Task<string> CreateOriginEventAsync(string calendarId, OriginEventDraft draft, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(calendarId)) throw new ArgumentException("calendarId required.", nameof(calendarId));
        ArgumentNullException.ThrowIfNull(draft);

        var (s, e, tz) = GraphDateFormat.For(draft.Start, draft.End, draft.TimeZoneId, draft.IsAllDay);
        var json = new JObject
        {
            ["subject"] = draft.Subject,
            ["start"] = new JObject { ["dateTime"] = s, ["timeZone"] = tz },
            ["end"] = new JObject { ["dateTime"] = e, ["timeZone"] = tz },
            ["isAllDay"] = draft.IsAllDay,
            ["showAs"] = draft.ShowAs,
        };
        // Body and location are the ORIGIN's privilege (spec §4) — they can never reach a
        // replica because ReplicaDraft cannot represent them.
        if (draft.BodyHtml.Length > 0)
            json["body"] = new JObject { ["contentType"] = "html", ["content"] = draft.BodyHtml };
        if (draft.Location.Length > 0)
            json["location"] = new JObject { ["displayName"] = draft.Location };

        var response = await _io.SendJsonAsync(HttpMethod.Post,
            $"me/calendars/{Uri.EscapeDataString(calendarId)}/events",
            json.ToString(Formatting.None), ct).ConfigureAwait(false);
        return response["id"]?.Value<string>() ?? "";
    }

    public async Task<IReadOnlyList<ReplicaEventRef>> ListReplicasInWindowAsync(
        string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(calendarId)) throw new ArgumentException("calendarId required.", nameof(calendarId));

        var start = fromUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var end = toUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        // ENGINE SEPARATION (spec §7): the expand filters by ZmReplicaOf EXCLUSIVELY, so a
        // CalImport (pair mirror) event can never appear in this engine's view — and therefore
        // can never be deleted or re-bound by it. The client-side guard below re-checks it.
        var expand = $"singleValueExtendedProperties($filter=id eq '{GraphJsonHttp.EscapeOData(_replicaPropId)}')";
        var url =
            $"me/calendars/{Uri.EscapeDataString(calendarId)}/calendarView" +
            $"?startDateTime={Uri.EscapeDataString(start)}" +
            $"&endDateTime={Uri.EscapeDataString(end)}" +
            $"&$select=id" +
            $"&$expand={Uri.EscapeDataString(expand)}" +
            $"&$top=50";

        var result = new List<ReplicaEventRef>();
        while (!string.IsNullOrEmpty(url))
        {
            var json = await _io.SendJsonAsync(HttpMethod.Get, url, null, ct).ConfigureAwait(false);
            foreach (var ev in GraphJsonHttp.RequireCollection(json, url))
            {
                var eventId = ev["id"]?.Value<string>() ?? "";
                if (eventId.Length == 0) continue;

                // Client-side ownership proof: only events whose expanded properties REALLY
                // carry ZmReplicaOf are returned (mis-honored server filters become no-ops).
                var sourceId = ReadMark(ev, _replicaPropId);
                if (sourceId.Length == 0) continue;

                result.Add(new ReplicaEventRef { EventId = eventId, SourceEventId = sourceId });
            }
            url = json["@odata.nextLink"]?.Value<string>() ?? "";
        }
        return result;
    }

    private string ExpandMarks() =>
        $"singleValueExtendedProperties($filter=id eq '{GraphJsonHttp.EscapeOData(_replicaPropId)}' " +
        $"or id eq '{GraphJsonHttp.EscapeOData(_rulePropId)}' " +
        $"or id eq '{GraphJsonHttp.EscapeOData(_calImportPropId)}')";

    private SourceEventSnapshot MapSnapshot(JToken ev)
    {
        var id = ev["id"]?.Value<string>() ?? "";
        var stableSeed = ev["iCalUId"]?.Value<string>();
        if (string.IsNullOrEmpty(stableSeed))
            stableSeed = id;

        var start = ParseGraphDateTime(ev["start"]);
        var end = ParseGraphDateTime(ev["end"]);

        return new SourceEventSnapshot
        {
            GraphEventId = id,
            StableId = id.Length == 0 ? "" : OccurrenceId.For(stableSeed!, start),
            Subject = ev["subject"]?.Value<string>() ?? "",
            Start = start,
            End = end,
            TimeZoneId = ev["start"]?["timeZone"]?.Value<string>() ?? "UTC",
            IsAllDay = ev["isAllDay"]?.Value<bool>() ?? false,
            ShowAs = ev["showAs"]?.Value<string>() ?? "busy",
            IsCancelled = ev["isCancelled"]?.Value<bool>() ?? false,
            IsOrganizer = ev["isOrganizer"]?.Value<bool>() ?? false,
            HasAttendees = (ev["attendees"] as JArray)?.Count > 0,
            HasReplicaMark = ReadMark(ev, _replicaPropId).Length > 0,
            HasCalImportMark = ReadMark(ev, _calImportPropId).Length > 0,
            RuleProcessedBy = ReadMark(ev, _rulePropId),
        };
    }

    private static string ReadMark(JToken ev, string propertyId)
    {
        if (ev["singleValueExtendedProperties"] is not JArray props)
            return "";
        foreach (var p in props)
        {
            if (string.Equals(p["id"]?.Value<string>(), propertyId, StringComparison.Ordinal))
                return p["value"]?.Value<string>() ?? "";
        }
        return "";
    }

    // Graph returns { dateTime, timeZone } with no offset. The replica reads always send
    // Prefer outlook.timezone="UTC", so the common case is the UTC fast path; the zone-mapping
    // fallback mirrors MicrosoftGraphProvider for robustness.
    private static DateTimeOffset ParseGraphDateTime(JToken? node)
    {
        if (node is null)
            return default;
        var dt = node["dateTime"]?.Value<string>();
        if (string.IsNullOrEmpty(dt))
            return default;

        var parsed = DateTime.Parse(dt, CultureInfo.InvariantCulture, DateTimeStyles.None);
        var zoneId = node["timeZone"]?.Value<string>() ?? "UTC";
        if (string.Equals(zoneId, "UTC", StringComparison.OrdinalIgnoreCase))
            return new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc));
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
            return new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), tz.GetUtcOffset(parsed));
        }
        catch (TimeZoneNotFoundException)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc));
        }
        catch (InvalidTimeZoneException)
        {
            return new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc));
        }
    }
}
