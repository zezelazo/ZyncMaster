using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SyncMaster.Graph;

public sealed class GraphCalendarTarget : ICalendarTarget
{
    private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0/";

    private readonly HttpClient                  _http;
    private readonly IGraphTokenProvider         _auth;
    private readonly string                      _extendedPropertyId;

    // Per-run cache of bodies already re-fetched from /me/events/{id}?$select=body.
    // The combined $select=body + $expand on extended properties intermittently returns
    // an empty body envelope; we currently re-fetch in that case. For events whose body
    // is legitimately empty, the re-fetch returns an empty string again — populating the
    // cache prevents re-issuing the same wasted GET if the same source id reappears in
    // FindByExternalIdsAsync during this run.
    private readonly Dictionary<string, string>  _bodyCacheBySourceId =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public GraphCalendarTarget(HttpClient http, IGraphTokenProvider auth, Guid extendedPropertyGuid)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _auth = auth ?? throw new ArgumentNullException(nameof(auth));

        if (_http.BaseAddress == null)
        {
            _http.BaseAddress = new Uri(GraphBaseUrl);
        }
        else if (_http.BaseAddress != new Uri(GraphBaseUrl))
        {
            // Silent fallthrough to a different host would send relative URLs to the
            // wrong endpoint and surface as confusing 404s. Fail fast on the contract.
            throw new ArgumentException(
                "HttpClient must have no BaseAddress or BaseAddress equal to " + GraphBaseUrl,
                nameof(http));
        }

        // Single-value extended property identifier shape used by Graph:
        //   String {GUID} Name CalImportSourceId
        _extendedPropertyId =
            $"String {{{extendedPropertyGuid.ToString("D").ToUpperInvariant()}}} Name CalImportSourceId";
    }

    public async Task<IReadOnlyList<CalendarTargetInfo>> ListCalendarsAsync(CancellationToken ct = default)
    {
        var json = await SendJsonAsync(HttpMethod.Get,
            "me/calendars?$select=id,name,isDefaultCalendar,owner", null, ct).ConfigureAwait(false);

        var list  = new List<CalendarTargetInfo>();
        var arr   = json["value"] as JArray ?? new JArray();
        foreach (var item in arr)
        {
            list.Add(new CalendarTargetInfo
            {
                Id          = item["id"]?.Value<string>()        ?? "",
                DisplayName = item["name"]?.Value<string>()      ?? "",
                IsDefault   = item["isDefaultCalendar"]?.Value<bool>() ?? false,
                Owner       = item["owner"]?["address"]?.Value<string>() ?? "",
            });
        }
        return list;
    }

    public async Task<CalendarTargetInfo> CreateCalendarAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Calendar name is required.", nameof(name));

        var body = new JObject(new JProperty("name", name)).ToString(Formatting.None);
        var json = await SendJsonAsync(HttpMethod.Post, "me/calendars", body, ct).ConfigureAwait(false);

        return new CalendarTargetInfo
        {
            Id          = json["id"]?.Value<string>()                ?? "",
            DisplayName = json["name"]?.Value<string>()              ?? name,
            IsDefault   = json["isDefaultCalendar"]?.Value<bool>()   ?? false,
            Owner       = json["owner"]?["address"]?.Value<string>() ?? "",
        };
    }

    public async Task<IReadOnlyDictionary<string, ExistingEventLookup>> FindByExternalIdsAsync(
        string calendarId, IReadOnlyList<string> externalIds, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(calendarId)) throw new ArgumentException("calendarId required.", nameof(calendarId));
        if (externalIds == null) throw new ArgumentNullException(nameof(externalIds));

        var result = new Dictionary<string, ExistingEventLookup>(StringComparer.Ordinal);

        // One query per id keeps URL length bounded. Graph supports $filter on
        // singleValueExtendedProperties with `any` semantics.
        foreach (var externalId in externalIds)
        {
            if (string.IsNullOrEmpty(externalId) || result.ContainsKey(externalId))
                continue;

            var filter =
                $"singleValueExtendedProperties/Any(ep:ep/id eq '{EscapeOData(_extendedPropertyId)}' " +
                $"and ep/value eq '{EscapeOData(externalId)}')";

            var expand =
                $"singleValueExtendedProperties($filter=id eq '{EscapeOData(_extendedPropertyId)}')";

            var url =
                $"me/calendars/{Uri.EscapeDataString(calendarId)}/events" +
                $"?$filter={Uri.EscapeDataString(filter)}" +
                $"&$expand={Uri.EscapeDataString(expand)}" +
                $"&$select=id,subject,body" +
                $"&$top=1";

            var json = await SendJsonAsync(HttpMethod.Get, url, null, ct).ConfigureAwait(false);
            var arr  = json["value"] as JArray;
            if (arr == null || arr.Count == 0) continue;

            var ev   = arr[0]!;
            var id   = ev["id"]?.Value<string>() ?? "";
            var body = ev["body"]?["content"]?.Value<string>() ?? "";

            if (id.Length == 0)
            {
                // A match without an id means we cannot address the event for update/delete.
                // Treating it as "not found" would create a duplicate on the next sync.
                throw new GraphRequestException(
                    $"Graph returned an event without an id for source id '{externalId}'.");
            }

            // The combined $select=body + $expand on extended properties intermittently
            // returns an empty body envelope from Graph. Re-fetching the body in isolation
            // is the only reliable way to avoid clobbering user-authored content when we
            // later merge the participants block. Cache the result per source id so events
            // with a legitimately empty body do not cost an extra GET on every appearance
            // of the same id within a single run.
            if (body.Length == 0)
            {
                if (!_bodyCacheBySourceId.TryGetValue(externalId, out var cached))
                {
                    var bodyJson = await SendJsonAsync(HttpMethod.Get,
                        $"me/events/{Uri.EscapeDataString(id)}?$select=body", null, ct).ConfigureAwait(false);
                    cached = bodyJson["body"]?["content"]?.Value<string>() ?? "";
                    _bodyCacheBySourceId[externalId] = cached;
                }
                body = cached;
            }

            result[externalId] = new ExistingEventLookup { Id = id, BodyHtml = body };
        }

        return result;
    }

    public async Task<string> CreateEventAsync(string calendarId, EventDraft draft, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(calendarId)) throw new ArgumentException("calendarId required.", nameof(calendarId));
        if (draft       == null)                   throw new ArgumentNullException(nameof(draft));

        var body = BuildEventJson(draft).ToString(Formatting.None);
        var json = await SendJsonAsync(HttpMethod.Post,
            $"me/calendars/{Uri.EscapeDataString(calendarId)}/events", body, ct).ConfigureAwait(false);

        return json["id"]?.Value<string>() ?? "";
    }

    public async Task UpdateEventAsync(string eventId, EventDraft draft, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(eventId)) throw new ArgumentException("eventId required.", nameof(eventId));
        if (draft == null)                      throw new ArgumentNullException(nameof(draft));

        var body = BuildEventJson(draft).ToString(Formatting.None);
        await SendJsonAsync(new HttpMethod("PATCH"),
            $"me/events/{Uri.EscapeDataString(eventId)}", body, ct).ConfigureAwait(false);
    }

    public async Task DeleteEventAsync(string eventId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(eventId)) throw new ArgumentException("eventId required.", nameof(eventId));

        await SendJsonAsync(HttpMethod.Delete,
            $"me/events/{Uri.EscapeDataString(eventId)}", null, ct).ConfigureAwait(false);
    }

    private JObject BuildEventJson(EventDraft draft)
    {
        string startDateTime, endDateTime, timeZone;
        if (draft.IsAllDay)
        {
            // Use the local-date component of the DateTimeOffset (what the user sees on
            // their calendar). UtcDateTime.Date shifts the day boundary in any non-UTC
            // offset and would post the event on the wrong day.
            var startDate = draft.Start.Date;
            var endDate   = draft.End.Date;
            if (endDate <= startDate)
                endDate = startDate.AddDays(1);

            startDateTime = startDate.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);
            endDateTime   = endDate.ToString("yyyy-MM-ddTHH:mm:ss.fffffff",   CultureInfo.InvariantCulture);
            timeZone      = "UTC";
        }
        else if (string.Equals(draft.TimeZoneId, "UTC", StringComparison.Ordinal))
        {
            // The draft builder falls back to "UTC" when it cannot map the original zone.
            // Sending the local wall-clock under that label would shift the event by the
            // offset; coerce to real UTC so datetime and timeZone agree.
            startDateTime = draft.Start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);
            endDateTime   = draft.End.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff",   CultureInfo.InvariantCulture);
            timeZone      = "UTC";
        }
        else
        {
            startDateTime = draft.Start.DateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);
            endDateTime   = draft.End.DateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffff",   CultureInfo.InvariantCulture);
            timeZone      = draft.TimeZoneId;
        }

        return new JObject
        {
            ["subject"]                    = draft.Subject,
            ["body"]                       = new JObject
            {
                ["contentType"] = "html",
                ["content"]     = draft.BodyHtml,
            },
            ["start"]                      = new JObject
            {
                ["dateTime"] = startDateTime,
                ["timeZone"] = timeZone,
            },
            ["end"]                        = new JObject
            {
                ["dateTime"] = endDateTime,
                ["timeZone"] = timeZone,
            },
            ["isAllDay"]                   = draft.IsAllDay,
            ["isReminderOn"]               = true,
            ["reminderMinutesBeforeStart"] = draft.ReminderMinutesBeforeStart,
            ["singleValueExtendedProperties"] = new JArray
            {
                new JObject
                {
                    ["id"]    = _extendedPropertyId,
                    ["value"] = draft.ExternalId,
                }
            },
        };
    }

    private async Task<JObject> SendJsonAsync(HttpMethod method, string url, string? jsonBody, CancellationToken ct)
    {
        const int maxAttempts        = 3;
        bool      unauthorizedRetried = false;
        bool      forceRefreshNext    = false;

        // Manual loop control instead of a for-loop: a 401 replay must not consume a
        // retry slot (otherwise a single token-refresh consumes one of the three attempts
        // budgeted for transient transport/throttling failures). We increment `attempt`
        // only on paths that actually count as a retry.
        int attempt = 1;
        while (true)
        {
            using var request = new HttpRequestMessage(method, url);
            if (jsonBody != null)
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            var token = await _auth.GetAccessTokenAsync(forceRefresh: forceRefreshNext, ct).ConfigureAwait(false);
            forceRefreshNext = false;
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            HttpResponseMessage? response = null;
            string               body     = "";

            try
            {
                response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                body     = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                // DNS, socket reset, TLS — transient transport failures deserve the same
                // backoff treatment as a 503.
                response?.Dispose();
                if (attempt >= maxAttempts)
                    throw new GraphRequestException(
                        $"Graph transport error after {attempt} attempts: {ex.Message}. URL={url}", ex);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
                attempt++;
                continue;
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // HttpClient surfaces request timeouts as TaskCanceledException with an
                // unsignalled token. Only retry when the caller did not cancel us.
                response?.Dispose();
                if (attempt >= maxAttempts)
                    throw new GraphRequestException(
                        $"Graph request timed out after {attempt} attempts. URL={url}", ex);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
                attempt++;
                continue;
            }

            using (response)
            {
                // 401: replay once with a forced token refresh. The default silent path
                // returns whatever bearer is currently cached, which is exactly the token
                // that just got rejected. WithForceRefresh(true) bypasses that cache.
                // This replay does not consume a retry slot — `attempt` is not incremented.
                if (response.StatusCode == HttpStatusCode.Unauthorized && !unauthorizedRetried)
                {
                    unauthorizedRetried = true;
                    forceRefreshNext    = true;
                    await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
                    continue;
                }

                // Second 401 (or 401 we already replayed): the refreshed token was also
                // rejected → credentials/consent problem, abort the run.
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new AuthenticationFailedException(
                        $"Graph returned 401 after refreshing the access token. " +
                        $"URL={url}, Body={Truncate(body, 200)}");
                }

                // Retry on throttling (429) and on the transient 5xx gateway/server errors
                // documented by Graph as safe to retry. 500/502/504 are bucketed with 503
                // because Graph's load balancer occasionally surfaces them for the same
                // transient conditions.
                var status = (int)response.StatusCode;
                if (status == 429 || status == 500 || status == 502 || status == 503 || status == 504)
                {
                    if (attempt >= maxAttempts)
                        throw new GraphRequestException(
                            $"Graph transient error after {attempt} attempts: {status} {response.ReasonPhrase}. " +
                            $"URL={url}, Body={Truncate(body, 200)}");

                    var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * attempt);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    attempt++;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    throw new GraphRequestException(
                        $"Graph request failed: {status} {response.ReasonPhrase}. " +
                        $"URL={url}, Body={Truncate(body, 500)}");

                if (string.IsNullOrEmpty(body))
                    return new JObject();

                return JObject.Parse(body);
            }
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";

    private static string EscapeOData(string value) => value.Replace("'", "''");
}
