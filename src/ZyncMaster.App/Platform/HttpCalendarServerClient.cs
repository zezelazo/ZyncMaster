using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ZyncMaster.App.Bridge;

namespace ZyncMaster.App.Platform;

// Thin HttpClient wrapper over the Server's calendar-account endpoints (Track A-2). Every request
// carries the signed-in user's identity access token as a Bearer (the IdentityBearer scheme) — never
// the device api key. Untested infrastructure (a live HTTP boundary, per CLAUDE.md); the connect
// orchestration is tested against ICalendarServerClient.
public sealed class HttpCalendarServerClient : ICalendarServerClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public HttpCalendarServerClient(HttpClient http, string serverBaseUrl)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrWhiteSpace(serverBaseUrl)) throw new ArgumentNullException(nameof(serverBaseUrl));
        _baseUrl = serverBaseUrl.TrimEnd('/');
    }

    public async Task<string?> StartGraphConnectAsync(
        string accessToken, string scope, int port, string nonce, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/calendar/connect/graph/start")
        {
            Content = JsonContent.Create(new { scope, port, nonce }),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            return null;

        var dto = await resp.Content.ReadFromJsonAsync<StartBundle>(JsonOptions, ct);
        return string.IsNullOrEmpty(dto?.AuthorizeUrl) ? null : dto.AuthorizeUrl;
    }

    public async Task<string?> UpgradeAccountScopeAsync(
        string accessToken, string accountId, int port, string nonce, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"{_baseUrl}/api/calendar/accounts/{Uri.EscapeDataString(accountId)}/upgrade-scope")
        {
            Content = JsonContent.Create(new { port, nonce }),
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            return null;

        var dto = await resp.Content.ReadFromJsonAsync<StartBundle>(JsonOptions, ct);
        return string.IsNullOrEmpty(dto?.AuthorizeUrl) ? null : dto.AuthorizeUrl;
    }

    public async Task<IReadOnlyList<CalendarAccountSummary>> ListCalendarAccountsAsync(
        string accessToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/calendar/accounts");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            return Array.Empty<CalendarAccountSummary>();

        var dtos = await resp.Content.ReadFromJsonAsync<List<AccountBundle>>(JsonOptions, ct);
        if (dtos is null)
            return Array.Empty<CalendarAccountSummary>();

        var list = new List<CalendarAccountSummary>(dtos.Count);
        foreach (var a in dtos)
        {
            list.Add(new CalendarAccountSummary(
                a.Id ?? "",
                a.Kind ?? "",
                a.Provider ?? "",
                a.AccountEmail ?? "",
                a.Scope ?? "",
                a.Status ?? "",
                a.DisplayName ?? ""));
        }
        return list;
    }

    private sealed class StartBundle
    {
        public string? AuthorizeUrl { get; set; }
    }

    private sealed class AccountBundle
    {
        public string? Id { get; set; }
        public string? Kind { get; set; }
        public string? Provider { get; set; }
        public string? AccountEmail { get; set; }
        public string? Scope { get; set; }
        public string? Status { get; set; }
        public string? DisplayName { get; set; }
    }
}
