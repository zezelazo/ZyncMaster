using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace ZyncMaster.Server;

// Identity (sign-in) OAuth endpoints — Microsoft only (plan v2 §A, body Phase 1). These run
// ALONGSIDE the legacy /connect calendar flow without touching it. The shape mirrors
// ConnectEndpoints (authorize redirect + state cookie + callback) but:
//   * scopes are IdentityScopes (openid email profile), never calendar scopes;
//   * the calendar refresh token is NEVER persisted — this proves identity only;
//   * the result is delivered to the desktop App over a loopback redirect via a one-time
//     handle (plan v2 §A-1) rather than a panel cookie;
//   * mode=web serves the Angular SPA: no loopback listener, so the callback lands the same
//     one-time handle on the SPA's FIXED same-origin path (/zync-web/auth/callback), mirroring
//     the magic-link web mode.
public static class IdentityConnectEndpoints
{
    // FIX 3 — per-IP fixed-window rate-limit policy for the unauthenticated token surfaces
    // (/identity/handle/redeem, /identity/refresh, /identity/magic-link/callback). These accept a
    // bearer-style secret (handle / refresh token / magic-link token) directly in the request, so
    // without a limiter they are grindable. Registered in Program.cs alongside the magic-link and
    // pairing limiters and attached via RequireRateLimiting; excess returns 429.
    public const string TokenRateLimitPolicy = "identity-token-ip";

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

            // mode=web marks the Angular SPA flow: there is no loopback listener, so no port is
            // required (it travels as 0, the same web marker the magic-link flow uses) and the
            // callback redirects to the SPA's fixed same-origin path instead of 127.0.0.1:{port}.
            var web = string.Equals(
                context.Request.Query["mode"].ToString(), "web", StringComparison.Ordinal);

            // The desktop App passes the loopback port it is listening on and a nonce it
            // generated; both are echoed back (port via the signed state, nonce in the final
            // redirect) so the App can verify the callback belongs to the request it started (I1).
            var port = 0;
            if (!web)
            {
                var portText = context.Request.Query["port"].ToString();
                if (!int.TryParse(portText, out port) || port < 1024 || port > 65535)
                    return Results.BadRequest("Invalid loopback port.");
            }

            var nonce = context.Request.Query["nonce"].ToString();
            if (string.IsNullOrEmpty(nonce))
                return Results.BadRequest("Missing nonce.");

            // Web mode may pass an explicit returnTo, but it is validated against a FIXED
            // allow-list (only the SPA's own callback path, relative or pinned under the
            // configured PublicBaseUrl). The actual redirect always uses the constant
            // IdentityLoopback.WebCallbackPath regardless, so the caller value never flows
            // into a Location header — this endpoint cannot become an open redirect.
            if (web)
            {
                var returnTo = context.Request.Query["returnTo"].ToString();
                if (!string.IsNullOrEmpty(returnTo) && !IsAllowedWebReturnTo(returnTo, options))
                    return Results.BadRequest("Invalid returnTo.");
            }

            // csrf is the value cross-checked between the signed state and the cookie at the
            // callback. The signed state additionally pins port + nonce + the web marker so
            // none of them can be tampered with in the redirect chain.
            var csrf = ApiKeyGenerator.Generate();
            var stateModel = new IdentityOAuthState { Port = port, Nonce = nonce, Csrf = csrf, Web = web };
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
            // result is intentionally ignored and never stored. If Microsoft rejects the
            // exchange (bad/expired client secret, redirect mismatch, etc.) surface the
            // NON-SECRET OAuth error/error_description as a 400 page instead of a blind 500,
            // so the failure is diagnosable. The exception message already scrubs token
            // material (see MicrosoftTokenService.BuildFailureMessage).
            TokenResult result;
            try
            {
                result = await tokenService.ExchangeIdentityCodeAsync(code, context.RequestAborted);
            }
            catch (ZyncMaster.Graph.AuthenticationFailedException ex)
            {
                var html =
                    "<!DOCTYPE html><html><body>" +
                    "<h1>Sign-in failed</h1>" +
                    "<p>The Microsoft token exchange was rejected.</p>" +
                    $"<p><code>{System.Net.WebUtility.HtmlEncode(ex.Message)}</code></p>" +
                    "</body></html>";
                return Results.Content(html, "text/html", statusCode: StatusCodes.Status400BadRequest);
            }

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

            // Mint the internal identity session and wrap it behind a one-time handle.
            // NOTE: the calendar refresh token (result.RefreshToken) is NOT persisted here — this
            // flow proves identity, it does not connect a calendar. The web marker and port come
            // from the SIGNED state (not the raw query) so the destination cannot be redirected
            // elsewhere; the nonce lets the client tie this callback to the request it initiated.
            // Both tails are shared with the magic-link flow: web mode lands the handle on the
            // SPA's FIXED same-origin callback path, desktop keeps the loopback redirect.
            var redirect = stateModel.Web
                ? await IdentityLoopback.IssueWebRedirectAsync(
                    user, stateModel.Nonce, identityTokens, handles, context.RequestAborted)
                : await IdentityLoopback.IssueLoopbackRedirectAsync(
                    user, stateModel.Port, stateModel.Nonce, identityTokens, handles, context.RequestAborted);

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
        }).RequireRateLimiting(TokenRateLimitPolicy);

        // Exchanges a valid refresh token for a freshly minted access token AND a rotated
        // refresh token (plan v2 §A-1). Anonymous: the refresh token IS the proof — possession
        // of an unexpired, unrevoked, un-replayed refresh token authorizes the renewal. The
        // presented token is revoked as part of the redeem (rotation), so a stolen-then-replayed
        // token is rejected on its second use. Unknown / expired / revoked / tampered → 401.
        app.MapPost("/identity/refresh", async (
            HttpContext context,
            IIdentityTokenService identityTokens,
            CancellationToken ct) =>
        {
            RefreshRequest? body;
            try
            {
                body = await context.Request.ReadFromJsonAsync<RefreshRequest>(ct);
            }
            catch (JsonException)
            {
                return Results.BadRequest("Invalid request body.");
            }

            if (body is null || string.IsNullOrEmpty(body.RefreshToken))
                return Results.BadRequest("Missing refreshToken.");

            // Redeem rotates: the old token is revoked and a fresh one is issued atomically.
            var outcome = await identityTokens.RedeemRefreshTokenAsync(body.RefreshToken, ct);
            if (outcome is null)
                return Results.Unauthorized();

            // Mint a new access token for the resolved user; hand back the rotated refresh token.
            var access = identityTokens.IssueAccessToken(outcome.User);

            return Results.Ok(new
            {
                accessToken = access.Token,
                newRefreshToken = outcome.NewRefreshToken,
            });
        }).RequireRateLimiting(TokenRateLimitPolicy);

        // Identity profile for the holder of a valid identity access token. Authentication is
        // enforced by the IdentityBearer scheme (RequireIdentityBearer); the caller is resolved
        // from the userId claim that scheme stamped, read via ICurrentUserAccessor. External
        // behavior is unchanged: 200 with the profile for a valid token, 401 otherwise.
        app.MapGet("/api/identity/me", async (
            ICurrentUserAccessor currentUser,
            IUserStore users,
            CancellationToken ct) =>
        {
            var userId = currentUser.UserId;
            if (string.IsNullOrEmpty(userId))
                return Results.Unauthorized();

            var user = await users.GetAsync(userId, ct);
            if (user is null)
                return Results.Unauthorized();

            return Results.Ok(new
            {
                userId = user.Id,
                email = string.IsNullOrEmpty(user.PrimaryEmail) ? user.Email : user.PrimaryEmail,
                displayName = user.DisplayName,
                plan = user.Plan,
            });
        }).RequireIdentityBearer();
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
            if (model is null ||
                string.IsNullOrEmpty(model.Nonce) || string.IsNullOrEmpty(model.Csrf))
            {
                return null;
            }
            // Web mode carries no loopback port (it travels as 0, the web marker); the desktop
            // flow keeps the strict loopback range.
            var portValid = model.Web
                ? model.Port == 0
                : model.Port >= 1024 && model.Port <= 65535;
            return portValid ? model : null;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException
            or FormatException or JsonException)
        {
            return null;
        }
    }

    // Allow-list for the optional web-mode returnTo. ONLY the SPA's own callback is accepted —
    // either the origin-relative constant path or that same path pinned under the configured
    // PublicBaseUrl. Exact ordinal match (no prefix/suffix tricks, no protocol-relative
    // "//evil.example/..." bypass). Anything else is rejected with 400; and because the real
    // redirect is built from the constant anyway, even an allow-listed value is never echoed.
    private static bool IsAllowedWebReturnTo(string returnTo, ServerOptions options)
    {
        if (string.Equals(returnTo, IdentityLoopback.WebCallbackPath, StringComparison.Ordinal))
            return true;

        if (!string.IsNullOrWhiteSpace(options.PublicBaseUrl))
        {
            var absolute = options.PublicBaseUrl.TrimEnd('/') + IdentityLoopback.WebCallbackPath;
            if (string.Equals(returnTo, absolute, StringComparison.Ordinal))
                return true;
        }

        return false;
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

        // True for the Angular SPA flow (mode=web): the callback redirects to the fixed
        // /zync-web/auth/callback path instead of the desktop loopback. Pinned in the signed
        // state so the destination class cannot be flipped in the redirect chain.
        [System.Text.Json.Serialization.JsonPropertyName("web")]
        public bool Web { get; set; }
    }

    private sealed class RedeemRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("handle")]
        public string? Handle { get; set; }
    }

    private sealed class RefreshRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("refreshToken")]
        public string? RefreshToken { get; set; }
    }
}
