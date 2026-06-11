using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZyncMaster.Engine;

// Raw-JSON REST client for /api/calendar/* (Calendar v2). Bearer-only (human management
// surface). Mirrors HttpPairsClient's transport/failure contract: non-2xx throws
// SyncClientException carrying the status code; the response body is returned verbatim
// as a string (the UIs parse it — see ICalendarV2Client for the rationale).
public sealed class HttpCalendarV2Client : ICalendarV2Client
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public HttpCalendarV2Client(HttpClient http, string serverBaseUrl)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (serverBaseUrl == null) throw new ArgumentNullException(nameof(serverBaseUrl));
        _baseUrl = serverBaseUrl.TrimEnd('/');
    }

    public Task<string> GetDayAsync(string bearer, string dateIso, CancellationToken ct)
    {
        if (bearer == null) throw new ArgumentNullException(nameof(bearer));
        if (string.IsNullOrWhiteSpace(dateIso)) throw new ArgumentNullException(nameof(dateIso));
        return SendAsync(HttpMethod.Get,
            $"/api/calendar/day?date={Uri.EscapeDataString(dateIso.Trim())}", bearer, null, ct);
    }

    public Task<string> CreateEventAsync(string bearer, string requestJson, CancellationToken ct)
    {
        if (bearer == null) throw new ArgumentNullException(nameof(bearer));
        if (string.IsNullOrWhiteSpace(requestJson)) throw new ArgumentNullException(nameof(requestJson));
        return SendAsync(HttpMethod.Post, "/api/calendar/events", bearer, requestJson, ct);
    }

    public Task<string> CreateReplicasAsync(string bearer, string accountId, string eventId, string requestJson, CancellationToken ct)
    {
        if (bearer == null) throw new ArgumentNullException(nameof(bearer));
        if (string.IsNullOrWhiteSpace(accountId)) throw new ArgumentNullException(nameof(accountId));
        if (string.IsNullOrWhiteSpace(eventId)) throw new ArgumentNullException(nameof(eventId));
        if (string.IsNullOrWhiteSpace(requestJson)) throw new ArgumentNullException(nameof(requestJson));
        return SendAsync(HttpMethod.Post,
            $"/api/calendar/events/{Uri.EscapeDataString(accountId)}/{Uri.EscapeDataString(eventId)}/replicas",
            bearer, requestJson, ct);
    }

    public Task<string> ListPrefixRulesAsync(string bearer, CancellationToken ct)
    {
        if (bearer == null) throw new ArgumentNullException(nameof(bearer));
        return SendAsync(HttpMethod.Get, "/api/calendar/prefix-rules", bearer, null, ct);
    }

    public Task<string> CreatePrefixRuleAsync(string bearer, string ruleJson, CancellationToken ct)
    {
        if (bearer == null) throw new ArgumentNullException(nameof(bearer));
        if (string.IsNullOrWhiteSpace(ruleJson)) throw new ArgumentNullException(nameof(ruleJson));
        return SendAsync(HttpMethod.Post, "/api/calendar/prefix-rules", bearer, ruleJson, ct);
    }

    public Task<string> UpdatePrefixRuleAsync(string bearer, string ruleId, string ruleJson, CancellationToken ct)
    {
        if (bearer == null) throw new ArgumentNullException(nameof(bearer));
        if (string.IsNullOrWhiteSpace(ruleId)) throw new ArgumentNullException(nameof(ruleId));
        if (string.IsNullOrWhiteSpace(ruleJson)) throw new ArgumentNullException(nameof(ruleJson));
        return SendAsync(HttpMethod.Put,
            $"/api/calendar/prefix-rules/{Uri.EscapeDataString(ruleId)}", bearer, ruleJson, ct);
    }

    public async Task DeletePrefixRuleAsync(string bearer, string ruleId, CancellationToken ct)
    {
        if (bearer == null) throw new ArgumentNullException(nameof(bearer));
        if (string.IsNullOrWhiteSpace(ruleId)) throw new ArgumentNullException(nameof(ruleId));
        await SendAsync(HttpMethod.Delete,
            $"/api/calendar/prefix-rules/{Uri.EscapeDataString(ruleId)}", bearer, null, ct);
    }

    private async Task<string> SendAsync(
        HttpMethod method, string path, string bearer, string? bodyJson, CancellationToken ct)
    {
        var url = $"{_baseUrl}{path}";
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        if (bodyJson != null)
            request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new SyncClientException(
                $"Calendar request {method} {url} failed with status {(int)response.StatusCode}: {text}",
                (int)response.StatusCode);

        return text;
    }
}
