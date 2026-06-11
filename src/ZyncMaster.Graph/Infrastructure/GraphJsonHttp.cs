using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ZyncMaster.Graph;

// Shared JSON transport for every Graph client in this library (GraphCalendarTarget,
// GraphReplicaClient, GraphEventResponder). Owns the retry budget (3 attempts for transient
// transport/throttling failures), the single 401 replay with a forced token refresh, the
// malformed-2xx → transient conversion, and the paged-read truncation guard. Extracted
// verbatim from GraphCalendarTarget so the battle-tested behavior is shared, not duplicated.
public sealed class GraphJsonHttp
{
    public const string GraphBaseUrl = "https://graph.microsoft.com/v1.0/";

    private readonly HttpClient          _http;
    private readonly IGraphTokenProvider _auth;

    public GraphJsonHttp(HttpClient http, IGraphTokenProvider auth)
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
    }

    // Sends and returns the parsed JSON. Non-2xx throws (401 after replay -> Authentication-
    // FailedException; 429/5xx after retries -> transient GraphRequestException; the rest ->
    // fatal GraphRequestException). preferUtcTimezone adds Prefer: outlook.timezone="UTC" so
    // calendarView reads normalize every event to UTC (the replica ContentHash relies on it).
    public async Task<JObject> SendJsonAsync(
        HttpMethod method, string url, string? jsonBody, CancellationToken ct,
        bool preferUtcTimezone = false)
    {
        var result = await SendCoreAsync(method, url, jsonBody, allowNotFound: false,
            preferUtcTimezone, ct).ConfigureAwait(false);
        return result!; // allowNotFound:false never returns null
    }

    // Same as SendJsonAsync but a 404 returns null instead of throwing — for "does this event
    // still exist?" probes (replica propagation, broken-link detection, respond preflight).
    public Task<JObject?> TrySendJsonAsync(
        HttpMethod method, string url, string? jsonBody, CancellationToken ct,
        bool preferUtcTimezone = false)
        => SendCoreAsync(method, url, jsonBody, allowNotFound: true, preferUtcTimezone, ct);

    private async Task<JObject?> SendCoreAsync(
        HttpMethod method, string url, string? jsonBody, bool allowNotFound,
        bool preferUtcTimezone, CancellationToken ct)
    {
        const int maxAttempts         = 3;
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
            if (preferUtcTimezone)
                request.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");

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
                        $"Graph transport error after {attempt} attempts: {ex.Message}. URL={url}", ex, isTransient: true);
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
                        $"Graph request timed out after {attempt} attempts. URL={url}", ex, isTransient: true);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
                attempt++;
                continue;
            }

            using (response)
            {
                // 401: replay once with a forced token refresh. The default silent path
                // returns whatever bearer is currently cached, which is exactly the token
                // that just got rejected. This replay does not consume a retry slot.
                if (response.StatusCode == HttpStatusCode.Unauthorized && !unauthorizedRetried)
                {
                    unauthorizedRetried = true;
                    forceRefreshNext    = true;
                    await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
                    continue;
                }

                // Second 401 (or 401 we already replayed): credentials/consent problem.
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    throw new AuthenticationFailedException(
                        $"Graph returned 401 after refreshing the access token. " +
                        $"URL={url}, Body={Truncate(body, 200)}");
                }

                // Probe semantics: a 404 means "the event/calendar no longer exists" — a
                // legitimate answer for the replica engine, never an error to retry.
                if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
                    return null;

                // Retry on throttling (429) and on the transient 5xx gateway/server errors
                // documented by Graph as safe to retry.
                var status = (int)response.StatusCode;
                if (status == 429 || status == 500 || status == 502 || status == 503 || status == 504)
                {
                    if (attempt >= maxAttempts)
                        throw new GraphRequestException(
                            $"Graph transient error after {attempt} attempts: {status} {response.ReasonPhrase}. " +
                            $"URL={url}, Body={Truncate(body, 200)}", isTransient: true);

                    var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * attempt);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    attempt++;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    throw new GraphRequestException(
                        $"Graph request failed: {status} {response.ReasonPhrase}. " +
                        $"URL={url}, Body={Truncate(body, 500)}");

                // An empty 2xx body is legitimate for DELETE/PATCH (204 No Content).
                if (string.IsNullOrEmpty(body))
                    return new JObject();

                // A 2xx that does not parse as JSON is a malformed/truncated response →
                // transient, so destructive callers abort instead of acting on a short read.
                try
                {
                    return JObject.Parse(body);
                }
                catch (JsonException ex)
                {
                    throw new GraphRequestException(
                        $"Graph returned a 2xx response that did not parse as JSON. " +
                        $"URL={url}, Body={Truncate(body, 200)}", ex, isTransient: true);
                }
            }
        }
    }

    // Guards against silent truncation of a paginated Graph read: every well-formed page
    // carries a "value" array (the last page is `value: []`); a 2xx page WITHOUT it is a
    // truncated read and must abort (transient), never be treated as end-of-pages.
    public static JArray RequireCollection(JObject json, string url)
    {
        if (json["value"] is JArray arr)
            return arr;

        throw new GraphRequestException(
            $"Graph paged read returned a 2xx response with no 'value' collection; " +
            $"treating as a truncated read rather than end-of-pages. URL={url}",
            isTransient: true);
    }

    public static string EscapeOData(string value) => value.Replace("'", "''");

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";
}
