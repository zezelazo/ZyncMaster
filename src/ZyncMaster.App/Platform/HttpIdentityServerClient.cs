using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ZyncMaster.App.Bridge;

namespace ZyncMaster.App.Platform;

// Thin HttpClient wrapper over the Server's identity endpoints. Untested infrastructure (a live
// HTTP boundary, per CLAUDE.md); the login orchestration is tested against IIdentityServerClient.
public sealed class HttpIdentityServerClient : IIdentityServerClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public HttpIdentityServerClient(HttpClient http, string serverBaseUrl)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (string.IsNullOrWhiteSpace(serverBaseUrl)) throw new ArgumentNullException(nameof(serverBaseUrl));
        _baseUrl = serverBaseUrl.TrimEnd('/');
    }

    public async Task<IdentityTokens?> RedeemHandleAsync(string handle, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync($"{_baseUrl}/identity/handle/redeem", new { handle }, ct);
        if (!resp.IsSuccessStatusCode)
            return null;

        var dto = await resp.Content.ReadFromJsonAsync<HandleBundle>(JsonOptions, ct);
        if (dto is null || string.IsNullOrEmpty(dto.AccessToken) || string.IsNullOrEmpty(dto.RefreshToken))
            return null;
        return new IdentityTokens(dto.AccessToken, dto.RefreshToken);
    }

    public async Task<RefreshResult?> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync($"{_baseUrl}/identity/refresh", new { refreshToken }, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized || !resp.IsSuccessStatusCode)
            return null;

        var dto = await resp.Content.ReadFromJsonAsync<RefreshBundle>(JsonOptions, ct);
        if (dto is null || string.IsNullOrEmpty(dto.AccessToken) || string.IsNullOrEmpty(dto.NewRefreshToken))
            return null;
        return new RefreshResult(dto.AccessToken, dto.NewRefreshToken);
    }

    public async Task<IdentityProfile?> GetMeAsync(string accessToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/identity/me");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            return null;

        var dto = await resp.Content.ReadFromJsonAsync<MeBundle>(JsonOptions, ct);
        if (dto is null)
            return null;
        return new IdentityProfile(dto.UserId, dto.Email, dto.DisplayName, dto.Plan);
    }

    public async Task<bool> RequestMagicLinkAsync(string email, int port, string nonce, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.PostAsJsonAsync(
                $"{_baseUrl}/identity/magic-link", new { email, port, nonce }, ct);
            // The Server returns a constant 202 whether or not the email exists; anything 2xx is a
            // successful request from the App's point of view.
            return resp.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private sealed class HandleBundle
    {
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
    }

    private sealed class RefreshBundle
    {
        public string? AccessToken { get; set; }
        public string? NewRefreshToken { get; set; }
    }

    private sealed class MeBundle
    {
        public string? UserId { get; set; }
        public string? Email { get; set; }
        public string? DisplayName { get; set; }
        public string? Plan { get; set; }
    }
}
