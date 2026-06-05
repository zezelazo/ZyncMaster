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

    public async Task<CalendarOption> CreateCalendarAsync(string name, CancellationToken ct = default)
    {
        var created = await _target.CreateCalendarAsync(name, ct).ConfigureAwait(false);
        return new CalendarOption
        {
            Id = created.Id,
            DisplayName = created.DisplayName,
            IsDefault = created.IsDefault,
            Owner = created.Owner,
        };
    }

    public async Task<MirrorResult> MirrorAsync(
        string calendarId,
        IReadOnlyList<AppointmentRecord> records,
        int reminderMinutes,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct = default,
        string pairId = "")
    {
        var mirror = new CalendarMirror(_target, new ImportPlanBuilder(), new EventDraftBuilder(new ParticipantBodyRenderer()));
        var outcome = await mirror
            .MirrorAsync(calendarId, records, reminderMinutes, fromUtc, toUtc, ct, pairId)
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

    public async Task<CleanupResult> CleanupManagedAsync(
        string calendarId,
        string pairId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(calendarId)) throw new ArgumentException("calendarId required.", nameof(calendarId));
        if (string.IsNullOrWhiteSpace(pairId))     throw new ArgumentException("pairId required.", nameof(pairId));

        // Enumerate ONLY the events this pair created in the (old) destination. Events without the
        // CalImportPairId == pairId property are never returned, so the cleanup can never touch the
        // user's own events nor another pair's events that happen to share the destination.
        var managed = await _target.ListManagedByPairAsync(calendarId, pairId, ct).ConfigureAwait(false);

        var deleted  = 0;
        var failures = new List<string>();
        foreach (var ev in managed)
        {
            try
            {
                await _target.DeleteEventAsync(ev.EventId, ct).ConfigureAwait(false);
                deleted++;
            }
            catch (Exception ex)
            {
                // Best-effort: a single failed delete must not abort the whole cleanup. The event
                // still carries the property, so a retry re-enumerates and re-deletes it.
                failures.Add($"Cleanup delete failed for event '{ev.EventId}' (source '{ev.SourceId}'): {ex.Message}");
            }
        }

        return new CleanupResult { Deleted = deleted, Failures = failures };
    }

    public async Task<int> CountManagedAsync(
        string calendarId,
        string pairId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(calendarId)) throw new ArgumentException("calendarId required.", nameof(calendarId));
        if (string.IsNullOrWhiteSpace(pairId))     throw new ArgumentException("pairId required.", nameof(pairId));

        var managed = await _target.ListManagedByPairAsync(calendarId, pairId, ct).ConfigureAwait(false);
        return managed.Count;
    }

    public async Task<IReadOnlyList<AppointmentRecord>> ReadWindowAsync(
        string calendarId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken ct = default,
        bool preserveLocalTime = false)
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
            var json = await GetJsonAsync(url, preserveLocalTime, ct).ConfigureAwait(false);

            // Every page of a Graph collection — first and every nextLink page — carries a
            // "value" array, even the last page (`value: []`, no nextLink). A 2xx page with
            // NO "value" key is malformed/truncated, NOT end-of-pages: silently treating it
            // as "no more data" would return a short source set, and the downstream mirror
            // would then sweep (delete) the events that belong to the pages we never read.
            // Abort the read with a transient error so /run and /api/sync stop BEFORE the
            // destructive mirror. `value: []` is the legitimate empty/last page and is fine.
            if (json["value"] is not JArray arr)
                throw new GraphRequestException(
                    $"Graph calendarView page returned a 2xx response with no 'value' " +
                    $"collection; treating as a truncated read rather than end-of-pages. URL={url}",
                    isTransient: true);

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
            // Per-occurrence upsert key. calendarView EXPANDS a recurring series into N
            // occurrences that all share the same iCalUId (and the same series event id),
            // so using the raw stableId would collapse the whole series onto ONE
            // AppointmentRecord.Id — losing N-1 occurrences and making the downstream mirror
            // send N updates onto a single destination event (orphaning the rest). Folding the
            // occurrence start into the id (exactly as the COM path does in CompleteCalendarReader
            // via OccurrenceId.For) gives each occurrence its own stable, distinct id so the
            // upsert + sweep treat each occurrence as its own event.
            Id = OccurrenceId.For(stableId, startOffset),
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

    private static bool IsTransientStatus(int status)
        => status == 429 || status == 500 || status == 502 || status == 503 || status == 504 || status == 408;

    private async Task<JObject> GetJsonAsync(string url, bool preserveLocalTime, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var token = await _tokens.GetAccessTokenAsync(false, ct).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // SYNC path: force UTC so every event normalizes to the same instant the destructive
        // mirror reconciles against. EXPORT path (preserveLocalTime): omit the Prefer header so
        // Graph returns each event in its ORIGINAL declared time zone (start.timeZone = the
        // event's real zone, start.dateTime = wall-clock in that zone). ParseGraphDateTime then
        // interprets it locally, so AppointmentRecord.Start carries the local clock time the user
        // sees — the same value CalExport's COM exporter writes to the .txt.
        if (!preserveLocalTime)
            request.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new GraphRequestException(
                $"Graph calendarView read failed: {(int)response.StatusCode} {response.ReasonPhrase}. URL={url}",
                // 429/5xx/408/timeout are retryable; anything else is a hard failure. We mark
                // the retryable band transient so the read aborts the run cleanly (Partial)
                // rather than letting an incomplete read flow into the sweep.
                isTransient: IsTransientStatus((int)response.StatusCode));

        // A 2xx with an empty or non-JSON body is a malformed/truncated read, not a valid
        // empty page (a real empty page is `{ "value": [] }`). Treat it as transient so the
        // paginated read aborts BEFORE the mirror instead of returning a short source set.
        if (string.IsNullOrEmpty(body))
            throw new GraphRequestException(
                $"Graph calendarView returned a 2xx response with an empty body; " +
                $"treating as a truncated read. URL={url}",
                isTransient: true);

        try
        {
            return JObject.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new GraphRequestException(
                $"Graph calendarView returned a 2xx response that did not parse as JSON; " +
                $"treating as a truncated read. URL={url}", ex, isTransient: true);
        }
    }
}
