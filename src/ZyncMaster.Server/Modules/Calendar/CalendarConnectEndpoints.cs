using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using ZyncMaster.Server.Modules.Calendar;

namespace ZyncMaster.Server;

// Calendar-account connection endpoints (Track A-2, plan body Phase 2 + v2 §A-5/§A-7/§B-7).
// These connect a Microsoft Graph (or outlook.com device) calendar account into the
// per-user pool managed by ICalendarAccountStore. Unlike the legacy /connect flow (single
// user, cookie + panel) and the identity sign-in flow (proves who you are, stores no
// calendar token), this flow:
//   * gates every mutation behind the IdentityBearer scheme — NEVER a cookie (v2 §A-5). The
//     userId comes from the validated token, never a query parameter.
//   * persists the calendar refresh token encrypted on the CalendarAccount.
//   * carries userId (+ accountId for upgrade) inside a SIGNED state blob so the anonymous
//     callback can verify ownership without re-authenticating (v2 §A-4/I6, §B-7).
//
// The refresh token is never exposed in any response, log, or redirect. The final loopback
// redirect only carries status + nonce; the App refreshes its account list over the bearer.
public static class CalendarConnectEndpoints
{
    // CSRF double-submit cookie for the calendar-connect flow. Distinct from the legacy
    // sm_oauth_state and the identity sm_identity_oauth_state cookies so the flows never collide.
    private const string StateCookieName = "sm_calendar_oauth_state";

    // DataProtection purpose for the signed state blob (v2 §A-5). New, dedicated purpose so a
    // calendar state can never be unprotected as an identity state and vice-versa.
    private const string StateProtectorPurpose = "ZyncMaster.CalendarOAuthState";

    public static void MapCalendarConnectEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // 1) Start a calendar OAuth connection. REQUIRES IdentityBearer — the userId is read
        //    from the token (ICurrentUserAccessor), never from a query parameter (v2 §A-5).
        app.MapGet("/calendar/connect/graph", (
            HttpContext context,
            ICurrentUserAccessor currentUser,
            IOptions<ServerOptions> opts,
            IDataProtectionProvider dp) =>
        {
            var options = opts.Value;
            var userId = currentUser.UserId;

            // scope selects the consent level: read-only or read/write.
            var scopeText = context.Request.Query["scope"].ToString();
            var scope = ParseScope(scopeText);
            if (scope is null)
                return Results.BadRequest("Invalid scope (expected 'read' or 'readwrite').");

            if (!TryReadLoopback(context, out var port, out var nonce, out var error))
                return Results.BadRequest(error);

            var calendarScopes = scope == AccountScope.ReadWrite
                ? options.CalendarReadWriteScopes
                : options.CalendarReadScopes;

            var csrf = ApiKeyGenerator.Generate();
            var stateModel = new CalendarOAuthState
            {
                UserId = userId,
                Scope = scope.ToString()!,
                Port = port,
                Nonce = nonce,
                Csrf = csrf,
            };
            var state = ProtectState(dp, stateModel);

            AppendCsrfCookie(context, csrf);

            // prompt=select_account: let the user pick which Microsoft account to connect.
            var authorizeUrl = BuildAuthorizeUrl(options, calendarScopes, "select_account", state);
            return Results.Redirect(authorizeUrl);
        }).RequireIdentityBearer();

        // 5) Start an incremental-consent upgrade (read -> read/write) for an EXISTING account
        //    (v2 §B-7/I6). REQUIRES IdentityBearer. The accountId is pinned into the signed
        //    state alongside the userId; the callback verifies that the account is owned by the
        //    userId before upgrading. prompt=consent forces the new (broader) grant.
        app.MapPost("/api/calendar/accounts/{id}/upgrade-scope", async (
            string id,
            HttpContext context,
            ICurrentUserAccessor currentUser,
            ICalendarAccountStore accounts,
            IOptions<ServerOptions> opts,
            IDataProtectionProvider dp,
            CancellationToken ct) =>
        {
            var options = opts.Value;
            var userId = currentUser.UserId;

            // The account must exist and belong to the caller. ICalendarAccountStore is already
            // user-scoped, so a null here means "not yours / nonexistent".
            var account = await accounts.GetAsync(id, ct);
            if (account is null)
                return Results.NotFound();

            if (!TryReadLoopback(context, out var port, out var nonce, out var error))
                return Results.BadRequest(error);

            var csrf = ApiKeyGenerator.Generate();
            var stateModel = new CalendarOAuthState
            {
                UserId = userId,
                Scope = AccountScope.ReadWrite.ToString(),
                AccountId = id,
                Port = port,
                Nonce = nonce,
                Csrf = csrf,
            };
            var state = ProtectState(dp, stateModel);

            AppendCsrfCookie(context, csrf);

            // prompt=consent re-prompts for the broader read/write grant even if the user
            // already consented to read-only.
            var authorizeUrl = BuildAuthorizeUrl(
                options, options.CalendarReadWriteScopes, "consent", state);
            return Results.Ok(new { authorizeUrl });
        }).RequireIdentityBearer();

        // 2) OAuth callback (anonymous — the signed state carries the userId). Handles BOTH the
        //    fresh-connect and the upgrade flows, distinguished by the presence of AccountId in
        //    the state. Validates error/state/csrf, exchanges the code with the right scopes,
        //    then persists the account/refresh-token UNDER THE STATE'S userId (pinned into the
        //    ambient accessor via the override item key, exactly like the legacy callback).
        app.MapGet("/calendar/connect/callback/graph", async (
            HttpContext context,
            IMicrosoftTokenService tokenService,
            ICalendarAccountStore accounts,
            IOptions<ServerOptions> opts,
            IDataProtectionProvider dp,
            CancellationToken ct) =>
        {
            var query = context.Request.Query;

            var error = query["error"].ToString();
            if (!string.IsNullOrEmpty(error))
            {
                var description = query["error_description"].ToString();
                return Results.Content(BuildErrorHtml(error, description), "text/html");
            }

            var stateModel = TryReadState(dp, query["state"].ToString());
            if (stateModel is null)
                return Results.BadRequest("Invalid OAuth state.");

            // Double-submit CSRF: the csrf in the signed state must match the cookie.
            var cookieCsrf = context.Request.Cookies[StateCookieName];
            if (string.IsNullOrEmpty(cookieCsrf) ||
                !string.Equals(cookieCsrf, stateModel.Csrf, StringComparison.Ordinal))
            {
                return Results.BadRequest("Invalid OAuth state.");
            }

            var code = query["code"].ToString();
            if (string.IsNullOrEmpty(code))
                return Results.BadRequest("Missing authorization code.");

            var scope = ParseScope(stateModel.Scope) ?? AccountScope.Read;
            var calendarScopes = scope == AccountScope.ReadWrite
                ? opts.Value.CalendarReadWriteScopes
                : opts.Value.CalendarReadScopes;

            // Exchange the code for the calendar refresh token + the account email. Scopes are
            // the ones the user actually consented to (pinned in the signed state).
            var result = await tokenService.ExchangeCalendarCodeAsync(code, calendarScopes, ct);
            var email = result.Email ?? result.UserPrincipalName ?? "";

            // Persist under the STATE's userId, never the ambient default. The IdentityBearer
            // token is not present on this anonymous callback, so we pin the user explicitly via
            // the override item the user-scoped store reads (same pattern as the legacy callback).
            context.Items[HttpContextCurrentUserAccessor.OverrideItemKey] = stateModel.UserId;

            if (!string.IsNullOrEmpty(stateModel.AccountId))
            {
                // Upgrade path (§B-7/I6): the account must still belong to the state's user.
                // The store is now scoped to that pinned user, so GetAsync returning non-null
                // proves ownership; a null means the account is not the user's -> reject.
                var existing = await accounts.GetAsync(stateModel.AccountId, ct);
                if (existing is null)
                    return Results.BadRequest("Account does not belong to the user.");

                await accounts.UpgradeScopeAsync(stateModel.AccountId, AccountScope.ReadWrite, ct);
                if (!string.IsNullOrEmpty(result.RefreshToken))
                    await accounts.UpdateRefreshTokenAsync(stateModel.AccountId, result.RefreshToken, ct);
            }
            else
            {
                // Fresh connect: add a new account to the pool.
                var account = new CalendarAccount
                {
                    Id = Guid.NewGuid().ToString("N"),
                    UserId = stateModel.UserId,
                    Kind = AccountKind.Graph,
                    Provider = "microsoft",
                    AccountEmail = email,
                    Authority = opts.Value.Authority,
                    Scope = scope,
                    DisplayName = string.IsNullOrEmpty(result.DisplayName) ? email : result.DisplayName,
                    Status = "active",
                    ConnectedAt = DateTimeOffset.UtcNow,
                };
                await accounts.AddAsync(account, result.RefreshToken, ct);
            }

            // Loopback back to the App. No tokens here — only status + nonce. The App refreshes
            // its account list over the bearer; it does not need calendar tokens.
            var redirect =
                $"http://127.0.0.1:{stateModel.Port}/calendar/callback" +
                $"?status=connected&nonce={Uri.EscapeDataString(stateModel.Nonce)}";
            return Results.Redirect(redirect);
        });

        // 3) List the caller's connected calendar accounts. REQUIRES IdentityBearer. Never
        //    returns the refresh token.
        app.MapGet("/api/calendar/accounts", async (
            ICalendarAccountStore accounts,
            CancellationToken ct) =>
        {
            var list = await accounts.ListAsync(ct);
            return Results.Ok(list.Select(a => new
            {
                id = a.Id,
                kind = a.Kind.ToString(),
                provider = a.Provider,
                accountEmail = a.AccountEmail,
                scope = a.Scope.ToString(),
                status = a.Status,
                displayName = a.DisplayName,
            }));
        }).RequireIdentityBearer();

        // 4) Disconnect a calendar account. REQUIRES IdentityBearer. Best-effort revoke the
        //    grant at the IdP (§A-7), mark revoked, then delete the account + token. The store
        //    is user-scoped, so a cross-user id resolves to null and the call is a no-op.
        app.MapDelete("/api/calendar/accounts/{id}", async (
            string id,
            ICalendarAccountStore accounts,
            CancellationToken ct) =>
        {
            var account = await accounts.GetAsync(id, ct);
            if (account is null)
                return Results.NotFound();

            // §A-7 best-effort IdP revoke. We fetch the refresh token (if any) and attempt to
            // revoke the grant before deleting our copy, marking the account revoked first so a
            // failure mid-way still leaves an auditable terminal state. The actual call to the
            // Microsoft/Google revoke endpoint is encapsulated in RevokeAtIdpAsync.
            var refreshToken = await accounts.GetRefreshTokenAsync(id, ct);
            if (!string.IsNullOrEmpty(refreshToken))
            {
                await accounts.UpdateStatusAsync(id, "revoked", ct);
                await RevokeAtIdpAsync(account, refreshToken, ct);
            }

            // TODO (Track A-3): disable any sync pairs that reference this account before the
            // delete, so a removed account does not leave dangling pair references.
            await accounts.RemoveAsync(id, ct);

            return Results.NoContent();
        }).RequireIdentityBearer();

        // 6) Connect an outlook.com calendar that is reached through a paired device (no Graph
        //    OAuth — the device holds the credentials). REQUIRES IdentityBearer. The deviceId
        //    MUST belong to the caller (IDeviceStore is user-scoped, so a foreign/unknown id
        //    resolves to null -> 400).
        app.MapPost("/calendar/connect/outlook-com", async (
            HttpContext context,
            ICurrentUserAccessor currentUser,
            IDeviceStore devices,
            ICalendarAccountStore accounts,
            CancellationToken ct) =>
        {
            OutlookComRequest? body;
            try
            {
                body = await context.Request.ReadFromJsonAsync<OutlookComRequest>(ct);
            }
            catch (JsonException)
            {
                return Results.BadRequest("Invalid request body.");
            }

            if (body is null || string.IsNullOrEmpty(body.DeviceId))
                return Results.BadRequest("Missing deviceId.");

            // Ownership: a device that is not the caller's resolves to null here.
            var device = await devices.GetAsync(body.DeviceId, ct);
            if (device is null)
                return Results.BadRequest("Unknown or unauthorized deviceId.");

            var account = new CalendarAccount
            {
                Id = Guid.NewGuid().ToString("N"),
                UserId = currentUser.UserId,
                Kind = AccountKind.OutlookCom,
                Provider = "outlook-com",
                AccountEmail = "",
                Scope = AccountScope.ReadWrite,
                DeviceId = body.DeviceId,
                DisplayName = device.Name,
                Status = "active",
                ConnectedAt = DateTimeOffset.UtcNow,
            };
            var added = await accounts.AddAsync(account, refreshToken: null, ct);

            return Results.Ok(new
            {
                id = added.Id,
                kind = added.Kind.ToString(),
                provider = added.Provider,
                scope = added.Scope.ToString(),
                status = added.Status,
                displayName = added.DisplayName,
            });
        }).RequireIdentityBearer();
    }

    // Best-effort revocation of the calendar grant at the identity provider (§A-7). For
    // Microsoft there is no public per-refresh-token revoke endpoint (revocation is
    // user-driven via account.live.com / Entra), so today this is a no-op placeholder that
    // never throws — the local token is always deleted by the caller regardless. Google DOES
    // expose https://oauth2.googleapis.com/revoke; wire it here when the Google provider lands.
    // Encapsulated so the endpoint stays declarative and a real revoke is a localized change.
    private static Task RevokeAtIdpAsync(CalendarAccount account, string refreshToken, CancellationToken ct)
    {
        // TODO (§A-7): call the provider revoke endpoint:
        //   * Google  -> POST https://oauth2.googleapis.com/revoke?token={refreshToken}
        //   * Microsoft -> no programmatic per-token revoke; rely on local delete + user action.
        // Must remain best-effort: swallow transport failures so a disconnect always completes.
        return Task.CompletedTask;
    }

    private static AccountScope? ParseScope(string? scopeText) => scopeText?.ToLowerInvariant() switch
    {
        "read" => AccountScope.Read,
        "readwrite" => AccountScope.ReadWrite,
        _ => null,
    };

    private static bool TryReadLoopback(HttpContext context, out int port, out string nonce, out string error)
    {
        port = 0;
        nonce = "";
        error = "";

        var portText = context.Request.Query["port"].ToString();
        if (!int.TryParse(portText, out port) || port < 1024 || port > 65535)
        {
            error = "Invalid loopback port.";
            return false;
        }

        nonce = context.Request.Query["nonce"].ToString();
        if (string.IsNullOrEmpty(nonce))
        {
            error = "Missing nonce.";
            return false;
        }

        return true;
    }

    private static void AppendCsrfCookie(HttpContext context, string csrf)
    {
        context.Response.Cookies.Append(StateCookieName, csrf, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            // Secure cookies do not round-trip over http (the test host); gate on the request
            // scheme so https in production still gets Secure.
            Secure = context.Request.IsHttps,
        });
    }

    private static string BuildAuthorizeUrl(
        ServerOptions options, string scopes, string prompt, string state) =>
        $"{options.Authority.TrimEnd('/')}/authorize" +
        $"?client_id={Uri.EscapeDataString(options.MicrosoftClientId)}" +
        "&response_type=code" +
        $"&redirect_uri={Uri.EscapeDataString(options.CalendarRedirectUri)}" +
        "&response_mode=query" +
        $"&scope={Uri.EscapeDataString(scopes)}" +
        $"&prompt={Uri.EscapeDataString(prompt)}" +
        $"&state={Uri.EscapeDataString(state)}";

    private static string BuildErrorHtml(string error, string? description) =>
        "<!DOCTYPE html><html><body>" +
        "<h1>Calendar connection failed</h1>" +
        $"<p>{System.Net.WebUtility.HtmlEncode(error)}</p>" +
        (string.IsNullOrEmpty(description)
            ? ""
            : $"<p>{System.Net.WebUtility.HtmlEncode(description)}</p>") +
        "</body></html>";

    private static string ProtectState(IDataProtectionProvider dp, CalendarOAuthState state)
    {
        var protector = dp.CreateProtector(StateProtectorPurpose);
        return ToBase64Url(protector.Protect(JsonSerializer.SerializeToUtf8Bytes(state)));
    }

    private static CalendarOAuthState? TryReadState(IDataProtectionProvider dp, string? stateText)
    {
        if (string.IsNullOrEmpty(stateText))
            return null;

        try
        {
            var protector = dp.CreateProtector(StateProtectorPurpose);
            var json = protector.Unprotect(FromBase64Url(stateText));
            var model = JsonSerializer.Deserialize<CalendarOAuthState>(json);
            if (model is null ||
                string.IsNullOrEmpty(model.UserId) ||
                model.Port < 1024 || model.Port > 65535 ||
                string.IsNullOrEmpty(model.Nonce) ||
                string.IsNullOrEmpty(model.Csrf))
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

    // Signed-state payload pinned into the protected blob carried to Microsoft as ?state. The
    // UserId proves ownership at the anonymous callback (the bearer is not present there);
    // AccountId is set only on the upgrade flow and the callback verifies it belongs to UserId.
    private sealed class CalendarOAuthState
    {
        [System.Text.Json.Serialization.JsonPropertyName("userId")]
        public string UserId { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("scope")]
        public string Scope { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("accountId")]
        public string? AccountId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("port")]
        public int Port { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("nonce")]
        public string Nonce { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("csrf")]
        public string Csrf { get; set; } = "";
    }

    private sealed class OutlookComRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("deviceId")]
        public string? DeviceId { get; set; }
    }
}
