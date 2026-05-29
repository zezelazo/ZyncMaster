using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ZyncMaster.Graph;

namespace ZyncMaster.Server;

public sealed class MicrosoftTokenService : IMicrosoftTokenService
{
    private readonly HttpClient _http;
    private readonly ServerOptions _options;
    private readonly ISecretProvider _secret;

    public MicrosoftTokenService(HttpClient http, IOptions<ServerOptions> opts, ISecretProvider secret)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentNullException.ThrowIfNull(opts);
        _options = opts.Value ?? throw new ArgumentNullException(nameof(opts));
        _secret = secret ?? throw new ArgumentNullException(nameof(secret));
    }

    public Task<TokenResult> ExchangeCodeAsync(string code, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        var form = new Dictionary<string, string>
        {
            ["client_id"] = _options.MicrosoftClientId,
            ["client_secret"] = _secret.GetMicrosoftClientSecret(),
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _options.RedirectUri,
            ["scope"] = _options.Scopes,
        };
        return PostAsync(form, fallbackRefreshToken: null, ct);
    }

    public Task<TokenResult> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);
        var form = new Dictionary<string, string>
        {
            ["client_id"] = _options.MicrosoftClientId,
            ["client_secret"] = _secret.GetMicrosoftClientSecret(),
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = _options.Scopes,
        };
        return PostAsync(form, fallbackRefreshToken: refreshToken, ct);
    }

    private async Task<TokenResult> PostAsync(
        Dictionary<string, string> form,
        string? fallbackRefreshToken,
        CancellationToken ct)
    {
        var endpoint = $"{_options.Authority.TrimEnd('/')}/token";
        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync(endpoint, content, ct).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new AuthenticationFailedException(
                $"Microsoft token endpoint returned {(int)response.StatusCode}: {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var accessToken = GetString(root, "access_token") ?? "";

        // Microsoft may omit refresh_token on refresh; keep the one we sent in that case.
        var refreshToken = GetString(root, "refresh_token");
        if (string.IsNullOrEmpty(refreshToken))
            refreshToken = fallbackRefreshToken ?? "";

        var expiresIn = root.TryGetProperty("expires_in", out var expEl) && expEl.TryGetInt32(out var secs)
            ? secs
            : 0;

        var upn = TryGetUpnFromIdToken(GetString(root, "id_token"));

        return new TokenResult
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            UserPrincipalName = upn,
        };
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    // Best-effort: decode the JWT payload of an id_token and read a UPN-like claim.
    // Returns null on any malformed input; callers tolerate a null UPN (the store
    // falls back to a "default" key for the single-user scenario).
    private static string? TryGetUpnFromIdToken(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken))
            return null;

        var parts = idToken.Split('.');
        if (parts.Length < 2)
            return null;

        try
        {
            var payload = Base64UrlDecode(parts[1]);
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            return GetString(root, "preferred_username")
                ?? GetString(root, "upn")
                ?? GetString(root, "email");
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}
