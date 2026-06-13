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

    public Task<TokenResult> ExchangeIdentityCodeAsync(string code, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        // Identity exchange: request only the identity scopes against the identity redirect
        // URI. We never persist the returned refresh token — pass fallbackRefreshToken: null
        // so the result simply carries whatever the endpoint returned (we only read the
        // id_token identity claims downstream).
        var form = new Dictionary<string, string>
        {
            ["client_id"] = _options.MicrosoftClientId,
            ["client_secret"] = _secret.GetMicrosoftClientSecret(),
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _options.IdentityRedirectUri,
            ["scope"] = _options.IdentityScopes,
        };
        return PostAsync(form, fallbackRefreshToken: null, ct);
    }

    public Task<TokenResult> ExchangeCalendarCodeAsync(
        string code, string scopes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentException.ThrowIfNullOrEmpty(scopes);
        // Calendar-connect exchange (Track A-2): the caller passes the EXACT scopes the user
        // consented to (read or read/write). offline_access in those scopes is what makes the
        // endpoint return a refresh token — which we DO persist on the CalendarAccount. The
        // calendar redirect URI is distinct from both the legacy and identity redirect URIs.
        var form = new Dictionary<string, string>
        {
            ["client_id"] = _options.MicrosoftClientId,
            ["client_secret"] = _secret.GetMicrosoftClientSecret(),
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _options.CalendarRedirectUri,
            ["scope"] = scopes,
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
            // FIX A — classify the failure so a throttle / server blip during a refresh is RETRIED,
            // not surfaced to the user as "reconnect". A 429 or 5xx from the token endpoint is
            // transient: throw a GraphRequestException with IsTransient=true (which
            // SyncErrorClassifier maps to Transient, so the run retries and the export endpoints
            // answer 503). Only a real OAuth failure (400/401 carrying invalid_grant /
            // interaction_required / consent_required, or a 401/403 with no usable diagnostics)
            // stays AuthenticationFailedException -> UserRecoverable ("the user must reconnect").
            var status = (int)response.StatusCode;

            // SECURITY (M1): never embed the raw token-endpoint response body in the thrown
            // message — it can carry token material and leaks into logs/telemetry. Surface
            // only the status code plus, at most, the OAuth error/error_description fields
            // (which by spec are non-secret diagnostics), never any *_token value.
            var message = BuildFailureMessage(response.StatusCode, body);

            if (IsTransientStatus(status))
            {
                // Respect Retry-After (delta or absolute date) so callers can honour the server's
                // backoff hint; default to a small delay when the header is absent.
                var retryAfter = ResolveRetryAfter(response);
                throw new GraphRequestException(
                    retryAfter is { } ra
                        ? $"{message} (transient; retry after {(int)ra.TotalSeconds}s)"
                        : $"{message} (transient)",
                    isTransient: true);
            }

            throw new AuthenticationFailedException(message);
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

        var identity = ParseIdToken(GetString(root, "id_token"));

        return new TokenResult
        {
            AccessToken = accessToken,
            RefreshToken = refreshToken,
            ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
            UserPrincipalName = identity.Upn,
            Subject = identity.Subject,
            Email = identity.Email,
            DisplayName = identity.DisplayName,
            EmailVerified = identity.EmailVerified,
            TenantId = identity.TenantId,
            EmailDomainOwnerVerified = identity.EmailDomainOwnerVerified,
        };
    }

    // Builds a non-secret failure message from the token-endpoint response. Tries to parse
    // the standard OAuth "error" / "error_description" fields out of a JSON body and appends
    // only those; if the body is not parseable JSON or carries no such fields, the message is
    // just the status code. The raw body is NEVER included (it may contain *_token values).
    private static string BuildFailureMessage(System.Net.HttpStatusCode status, string body)
    {
        var prefix = $"Microsoft token endpoint returned {(int)status}";
        if (string.IsNullOrWhiteSpace(body))
            return prefix + ".";

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return prefix + ".";

            var error = GetString(root, "error");
            var description = GetString(root, "error_description");
            if (string.IsNullOrEmpty(error) && string.IsNullOrEmpty(description))
                return prefix + ".";

            var detail = string.IsNullOrEmpty(description)
                ? error
                : (string.IsNullOrEmpty(error) ? description : $"{error}: {description}");
            return $"{prefix} ({detail}).";
        }
        catch (JsonException)
        {
            return prefix + ".";
        }
    }

    // Transient = retryable token-endpoint statuses. 429 (throttling) plus the gateway/5xx family.
    // A 408 (request timeout) is also transient. Everything else (notably 400/401/403) is a real
    // OAuth/auth failure the user must act on, so it stays UserRecoverable.
    private static bool IsTransientStatus(int status)
        => status is 429 or 408 or 500 or 502 or 503 or 504;

    // Resolves the Retry-After hint from the response: a delta (seconds) directly, or an absolute
    // date converted to a delta from now (never negative). Returns null when the header is absent
    // or already in the past.
    private static TimeSpan? ResolveRetryAfter(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        if (ra is null)
            return null;
        if (ra.Delta is { } delta && delta > TimeSpan.Zero)
            return delta;
        if (ra.Date is { } date)
        {
            var fromNow = date - DateTimeOffset.UtcNow;
            if (fromNow > TimeSpan.Zero)
                return fromNow;
        }
        return null;
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private readonly record struct IdTokenIdentity(
        string? Subject, string? Email, string? DisplayName, string? Upn,
        bool? EmailVerified, string? TenantId, bool? EmailDomainOwnerVerified);

    // Best-effort: decode the JWT payload of an id_token and read the identity claims.
    // Subject = oid (stable per-tenant object id) falling back to sub; email/upn from the
    // usual username-like claims; name from the "name" claim. Also lifts the account-linking
    // trust signals: email_verified, the tenant id (tid), and xms_edov (AAD email-domain-owner
    // -verified). Returns all-null on any malformed input; callers tolerate nulls (the
    // connected-account store falls back to a "default" key for the single-user scenario).
    private static IdTokenIdentity ParseIdToken(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken))
            return default;

        var parts = idToken.Split('.');
        if (parts.Length < 2)
            return default;

        try
        {
            var payload = Base64UrlDecode(parts[1]);
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var subject = GetString(root, "oid") ?? GetString(root, "sub");
            var email = GetString(root, "preferred_username")
                ?? GetString(root, "upn")
                ?? GetString(root, "email");
            var name = GetString(root, "name");
            return new IdTokenIdentity(
                subject, email, name, email,
                GetBool(root, "email_verified"),
                GetString(root, "tid"),
                GetBool(root, "xms_edov"));
        }
        catch (FormatException)
        {
            return default;
        }
        catch (JsonException)
        {
            return default;
        }
    }

    // Reads a claim that may be emitted as a JSON boolean OR as the string "true"/"false"
    // (different IdPs/Microsoft account types render email_verified either way). Returns null
    // when the claim is absent or unparseable, so the endpoint layer can apply its own default.
    private static bool? GetBool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el))
            return null;
        return el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(el.GetString(), out var b) => b,
            _ => null,
        };
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
