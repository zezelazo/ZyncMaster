using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace ZyncMaster.Server;

// Identity (sign-in) OAuth endpoints — Microsoft only (plan v2 §A, body Phase 1). These run
// ALONGSIDE the legacy /connect calendar flow without touching it. The shape mirrors
// ConnectEndpoints (authorize redirect + state cookie + callback) but:
//   * scopes are IdentityScopes (openid email profile), never calendar scopes;
//   * the calendar refresh token is NEVER persisted — this proves identity only;
//   * the result is delivered to the desktop App over a loopback redirect via a one-time
//     handle (plan v2 §A-1) rather than a panel cookie.
public static class IdentityConnectEndpoints
{
    // CSRF cross-check cookie for the identity flow (kept distinct from the legacy
    // sm_oauth_state so the two flows never collide).
    private const string StateCookieName = "sm_identity_oauth_state";

    // DataProtection purpose for the signed state blob (plan v2 §A-5 + finding I1).
    private const string StateProtectorPurpose = "ZyncMaster.IdentityOAuthState";

    public static void MapIdentityConnectEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/identity/connect/microsoft", (
            HttpContext context,
            IOptions<ServerOptions> opts,
            IDataProtectionProvider dp) =>
        {
            var options = opts.Value;

            // The App passes the loopback port it is listening on and a nonce it generated;
            // both are echoed back (port via the signed state, nonce in the final redirect)
            // so the App can verify the callback belongs to the request it started (I1).
            var portText = context.Request.Query["port"].ToString();
            if (!int.TryParse(portText, out var port) || port < 1024 || port > 65535)
                return Results.BadRequest("Invalid loopback port.");

            var nonce = context.Request.Query["nonce"].ToString();
            if (string.IsNullOrEmpty(nonce))
                return Results.BadRequest("Missing nonce.");

            // csrf is the value cross-checked between the signed state and the cookie at the
            // callback. The signed state additionally pins port + nonce so neither can be
            // tampered with in the redirect chain.
            var csrf = ApiKeyGenerator.Generate();
            var stateModel = new IdentityOAuthState { Port = port, Nonce = nonce, Csrf = csrf };
            var protector = dp.CreateProtector(StateProtectorPurpose);
            var state = ToBase64Url(
                protector.Protect(JsonSerializer.SerializeToUtf8Bytes(stateModel)));

            context.Response.Cookies.Append(StateCookieName, csrf, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                // Secure cookies do not round-trip over http (the test host); gate on the
                // request scheme so https in production still gets Secure.
                Secure = context.Request.IsHttps,
            });

            var authorizeUrl =
                $"{options.Authority.TrimEnd('/')}/authorize" +
                $"?client_id={Uri.EscapeDataString(options.MicrosoftClientId)}" +
                "&response_type=code" +
                $"&redirect_uri={Uri.EscapeDataString(options.IdentityRedirectUri)}" +
                "&response_mode=query" +
                $"&scope={Uri.EscapeDataString(options.IdentityScopes)}" +
                // Force the account picker, matching the legacy /connect behaviour.
                "&prompt=select_account" +
                $"&state={Uri.EscapeDataString(state)}";

            return Results.Redirect(authorizeUrl);
        });

        app.MapGet("/identity/connect/callback/microsoft", async (
            HttpContext context,
            IMicrosoftTokenService tokenService,
            IUserStore users,
            IIdentityTokenService identityTokens,
            IIdentityHandleStore handles,
            IDataProtectionProvider dp) =>
        {
            var query = context.Request.Query;

            var error = query["error"].ToString();
            if (!string.IsNullOrEmpty(error))
            {
                var description = query["error_description"].ToString();
                var html =
                    "<!DOCTYPE html><html><body>" +
                    "<h1>Sign-in failed</h1>" +
                    $"<p>{System.Net.WebUtility.HtmlEncode(error)}</p>" +
                    (string.IsNullOrEmpty(description)
                        ? ""
                        : $"<p>{System.Net.WebUtility.HtmlEncode(description)}</p>") +
                    "</body></html>";
                return Results.Content(html, "text/html");
            }

            // Unprotect + validate the signed state. A tampered/foreign blob fails to unprotect.
            var stateText = query["state"].ToString();
            var stateModel = TryReadState(dp, stateText);
            if (stateModel is null)
                return Results.BadRequest("Invalid OAuth state.");

            // Cross-check the csrf in the signed state against the cookie (double-submit).
            var cookieCsrf = context.Request.Cookies[StateCookieName];
            if (string.IsNullOrEmpty(cookieCsrf) ||
                !string.Equals(cookieCsrf, stateModel.Csrf, StringComparison.Ordinal))
            {
                return Results.BadRequest("Invalid OAuth state.");
            }

            var code = query["code"].ToString();
            if (string.IsNullOrEmpty(code))
                return Results.BadRequest("Missing authorization code.");

            // Identity exchange: identity scopes only; the calendar refresh token in the
            // result is intentionally ignored and never stored.
            var result = await tokenService.ExchangeIdentityCodeAsync(code, context.RequestAborted);

            var subject = string.IsNullOrEmpty(result.Subject)
                ? (result.UserPrincipalName ?? "")
                : result.Subject;
            var email = result.Email ?? result.UserPrincipalName ?? "";
            var displayName = result.DisplayName ?? email;

            // SECURITY (plan v2 §A-4): Microsoft is NOT trusted for auto-linking by email, so
            // we pass emailVerified:false on purpose. This guarantees a Microsoft sign-in
            // never silently merges into a pre-existing local account that happens to share
            // the same email — which would be an account-takeover vector. Explicit
            // cross-provider linking with proof-of-possession (a confirmation magic-link to
            // the email) is a later feature; until then a Microsoft login only ever resolves
            // to its own (provider, subject) user or creates a fresh one.
            var user = await users.UpsertByLoginAsync(
                "microsoft", subject, email, emailVerified: false, displayName, context.RequestAborted);

            // Mint the internal identity session: a short-lived access token plus a long-lived
            // refresh token. NOTE: the calendar refresh token (result.RefreshToken) is NOT
            // persisted here — this flow proves identity, it does not connect a calendar.
            var access = identityTokens.IssueAccessToken(user);
            var refresh = await identityTokens.IssueRefreshTokenAsync(user.Id, context.RequestAborted);

            // Wrap BOTH tokens behind a single one-time handle. The handle store takes a
            // string, so serialize the pair; the App deserializes it on redeem.
            var bundle = JsonSerializer.Serialize(new HandleBundle
            {
                AccessToken = access.Token,
                RefreshToken = refresh,
            });
            var handle = handles.IssueHandle(bundle);

            // Redirect to the App's loopback listener. The port comes from the SIGNED state
            // (not the raw query) so it cannot be redirected elsewhere; the nonce lets the App
            // tie this callback to the request it initiated.
            var redirect =
                $"http://127.0.0.1:{stateModel.Port}/identity/callback" +
                $"?handle={Uri.EscapeDataString(handle)}" +
                $"&nonce={Uri.EscapeDataString(stateModel.Nonce)}";

            return Results.Redirect(redirect);
        });

        // Exchanges a one-time handle for the wrapped {accessToken, refreshToken}. Anonymous:
        // possession of the (single-use, 60s) handle IS the proof. Returns 410 Gone on an
        // unknown/expired/already-consumed handle.
        app.MapPost("/identity/handle/redeem", async (HttpContext context, IIdentityHandleStore handles) =>
        {
            RedeemRequest? body;
            try
            {
                body = await context.Request.ReadFromJsonAsync<RedeemRequest>(context.RequestAborted);
            }
            catch (JsonException)
            {
                return Results.BadRequest("Invalid request body.");
            }

            if (body is null || string.IsNullOrEmpty(body.Handle))
                return Results.BadRequest("Missing handle.");

            var bundle = handles.ConsumeHandle(body.Handle);
            if (bundle is null)
                return Results.StatusCode(StatusCodes.Status410Gone);

            // The wrapped JSON ({accessToken, refreshToken}) is returned verbatim.
            return Results.Content(bundle, "application/json");
        });

        // Identity profile for the holder of a valid identity access token. The formal
        // IdentityBearer authentication scheme is a later task; here the Bearer is validated
        // inline via IIdentityTokenService so this endpoint can ship independently.
        app.MapGet("/api/identity/me", async (
            HttpContext context,
            IIdentityTokenService identityTokens,
            IUserStore users,
            CancellationToken ct) =>
        {
            var bearer = ExtractBearer(context);
            if (string.IsNullOrEmpty(bearer))
                return Results.Unauthorized();

            var principal = identityTokens.ValidateAccessToken(bearer);
            if (principal is null)
                return Results.Unauthorized();

            var user = await users.GetAsync(principal.UserId, ct);
            if (user is null)
                return Results.Unauthorized();

            return Results.Ok(new
            {
                userId = user.Id,
                email = string.IsNullOrEmpty(user.PrimaryEmail) ? user.Email : user.PrimaryEmail,
                displayName = user.DisplayName,
                plan = user.Plan,
            });
        });
    }

    private static IdentityOAuthState? TryReadState(IDataProtectionProvider dp, string? stateText)
    {
        if (string.IsNullOrEmpty(stateText))
            return null;

        try
        {
            var protector = dp.CreateProtector(StateProtectorPurpose);
            var json = protector.Unprotect(FromBase64Url(stateText));
            var model = JsonSerializer.Deserialize<IdentityOAuthState>(json);
            if (model is null || model.Port < 1024 || model.Port > 65535 ||
                string.IsNullOrEmpty(model.Nonce) || string.IsNullOrEmpty(model.Csrf))
            {
                return null;
            }
            return model;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException
            or FormatException or JsonException)
        {
            return null;
        }
    }

    private static string? ExtractBearer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (string.IsNullOrEmpty(header) || !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var token = header[prefix.Length..].Trim();
        return string.IsNullOrEmpty(token) ? null : token;
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    // Signed-state payload pinned into the protected blob that travels to Microsoft as ?state.
    private sealed class IdentityOAuthState
    {
        [System.Text.Json.Serialization.JsonPropertyName("port")]
        public int Port { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("nonce")]
        public string Nonce { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("csrf")]
        public string Csrf { get; set; } = "";
    }

    // The JSON wrapped behind the one-time handle and returned on redeem.
    private sealed class HandleBundle
    {
        [System.Text.Json.Serialization.JsonPropertyName("accessToken")]
        public string AccessToken { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("refreshToken")]
        public string RefreshToken { get; set; } = "";
    }

    private sealed class RedeemRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("handle")]
        public string? Handle { get; set; }
    }
}
