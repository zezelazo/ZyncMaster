using System.Globalization;
using System.Net.Http.Headers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ZyncMaster.Core;
using ZyncMaster.Graph;

namespace ZyncMaster.Server;

// Microsoft Graph implementation of both the calendar reader and writer for a single
// connected account. Writing reuses the Graph library's CalendarMirror over a
// GraphCalendarTarget. Reading is a focused calendarView GET issued here in the Server
// (the Graph library deliberately exposes no "read everything" call), so the Server
// owns the AppointmentRecord projection and the Graph project stays untouched.
public sealed class MicrosoftGraphProvider : ICalendarReader, ICalendarWriter
{
    private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0/";

    private readonly HttpClient _http;
    private readonly IGraphTokenProvider _tokens;
    private readonly ICalendarTarget _target;

    public MicrosoftGraphProvider(HttpClient http, IGraphTokenProvider tokens, ICalendarTarget target)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _target = target ?? throw new ArgumentNullException(nameof(target));
    }

    public async Task<IReadOnlyList<CalendarOption>> ListCalendarsAsync(CancellationToken ct = default)
    {
        var cals = await _target.ListCalendarsAsync(ct).ConfigureAwait(false);
        return cals.Select(c => new CalendarOption
        {
            Id = c.Id,
            DisplayName = c.DisplayName,
            IsDefault = c.IsDefault,
            Owner = c.Owner,
        }).ToList();
    }

    public async Task<MirrorResult> MirrorAsync(
        string calendarId,
        IReadOnlyList<AppointmentRecord> records,
        int reminderMinutes,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct = default)
    {
        var mirror = new CalendarMirror(_target, new ImportPlanBuilder(), new EventDraftBuilder(new ParticipantBodyRenderer()));
        var outcome = await mirror
            .MirrorAsync(calendarId, records, reminderMinutes, fromUtc, toUtc, ct)
            .ConfigureAwait(false);

        return new MirrorResult
        {
            Created = outcome.Created,
            Updated = outcome.Updated,
            Deleted = outcome.Deleted,
            Skipped = outcome.Skipped,
            Failures = outcome.Failures.Select(f => f.ToString()).ToList(),
            Partial = outcome.Partial,
        };
    }

    public async Task<IReadOnlyList<AppointmentRecord>> ReadWindowAsync(
        string calendarId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(calendarId))
            throw new ArgumentException("calendarId required.", nameof(calendarId));

        var start = fromUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var end = toUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        // calendarView expands recurrences and bounds results to the window so we never
        // enumerate the whole calendar. Select only the fields needed to build the record.
        var url =
            $"{GraphBaseUrl}me/calendars/{Uri.EscapeDataString(calendarId)}/calendarView" +
            $"?startDateTime={Uri.EscapeDataString(start)}" +
            $"&endDateTime={Uri.EscapeDataString(end)}" +
            "&$select=id,iCalUId,subject,body,start,end,isAllDay,isCancelled,organizer" +
            "&$top=50";

        var records = new List<AppointmentRecord>();

        while (!string.IsNullOrEmpty(url))
        {
            var json = await GetJsonAsync(url, ct).ConfigureAwait(false);
            var arr = json["value"] as JArray ?? new JArray();

            foreach (var ev in arr)
            {
                var record = MapEvent(ev);
                if (record is not null)
                    records.Add(record);
            }

            url = json["@odata.nextLink"]?.Value<string>() ?? "";
        }

        return records;
    }

    private static AppointmentRecord? MapEvent(JToken ev)
    {
        var id = ev["id"]?.Value<string>() ?? "";
        if (id.Length == 0)
            return null;

        // Prefer the stable iCalUId so the same series resolves to one record across runs;
        // fall back to the Graph event id.
        var stableId = ev["iCalUId"]?.Value<string>();
        if (string.IsNullOrEmpty(stableId))
            stableId = id;

        var subject = ev["subject"]?.Value<string>() ?? "";
        // Full body content (not the truncated bodyPreview) so Graph→Graph mirroring keeps
        // the complete description across cycles.
        var description = ev["body"]?["content"]?.Value<string>() ?? "";
        var isAllDay = ev["isAllDay"]?.Value<bool>() ?? false;
        var isCancelled = ev["isCancelled"]?.Value<bool>() ?? false;

        var organizerName = ev["organizer"]?["emailAddress"]?["name"]?.Value<string>() ?? "";
        var organizerEmail = ev["organizer"]?["emailAddress"]?["address"]?.Value<string>() ?? "";

        var startOffset = ParseGraphDateTime(ev["start"]);
        var endOffset = ParseGraphDateTime(ev["end"]);

        var duration = (int)Math.Round((endOffset - startOffset).TotalMinutes);
        if (duration < 0)
            duration = 0;

        var startTimeZone = ev["start"]?["timeZone"]?.Value<string>() ?? "UTC";

        return new AppointmentRecord
        {
            Id = stableId,
            Subject = subject,
            Description = description,
            IsAllDay = isAllDay,
            IsCancelled = isCancelled,
            OrganizerName = organizerName,
            OrganizerEmail = organizerEmail,
            Start = startOffset.DateTime,
            Duration = duration,
            StartOffset = startOffset,
            EndOffset = endOffset,
            StartTimeZoneId = startTimeZone,
            StartTimeZoneDisplayName = startTimeZone,
        };
    }

    // Graph returns { dateTime: "2026-05-29T10:00:00.0000000", timeZone: "UTC" }. The
    // dateTime carries no offset; interpret it in the declared zone, defaulting to UTC.
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
            var offset = tz.GetUtcOffset(parsed);
            return new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified), offset);
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

    private async Task<JObject> GetJsonAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var token = await _tokens.GetAccessTokenAsync(false, ct).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new GraphRequestException(
                $"Graph calendarView read failed: {(int)response.StatusCode} {response.ReasonPhrase}. URL={url}");

        return string.IsNullOrEmpty(body) ? new JObject() : JObject.Parse(body);
    }
}
