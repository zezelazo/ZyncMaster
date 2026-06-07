using FluentValidation;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

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

    // Flow mode carried INSIDE the signed (inforgeable) state. It decides whether the callback
    // enforces the double-submit CSRF cookie:
    //   * "web" — the browser-initiated GET /calendar/connect/graph 302 flow: the SAME browser
    //     does both legs, so the cookie round-trips and the double-submit check is meaningful.
    //   * "app" — the cookie-less App flow (POST /start and upgrade-scope return an authorizeUrl
    //     the App opens in the SYSTEM browser, a different cookie jar). The cookie never round-
    //     trips, so requiring it would break the connect. Integrity comes from the signed state
    //     (the server can't be tricked about the user) + the loopback nonce the App verifies.
    private const string ModeWeb = "web";
    private const string ModeApp = "app";

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

            // prompt=select_account: let the user pick which Microsoft account to connect. This is
            // the browser-initiated WEB flow (the same browser does both legs), so the state is
            // marked "web" and the double-submit CSRF cookie is enforced at the callback.
            var authorizeUrl = BuildConnectStartUrl(
                context, dp, options, userId, scope.Value, port, nonce, "select_account", ModeWeb);
            return Results.Redirect(authorizeUrl);
        }).RequireIdentityBearer();

        // 1b) Start a calendar OAuth connection and RETURN the authorize URL as JSON instead of a
        //     302. The desktop App holds an IdentityBearer but no browser cookie, so it cannot
        //     consume the redirect from (1); it calls this, then opens the returned authorizeUrl in
        //     the system browser. Behaviour is otherwise identical to (1) — same signed state, same
        //     CSRF cookie, same authorize URL — mirroring how upgrade-scope returns { authorizeUrl }.
        //     REQUIRES IdentityBearer: the userId is the token's, NEVER the body's.
        app.MapPost("/api/calendar/connect/graph/start", (
            ConnectGraphStartRequest? request,
            HttpContext context,
            ICurrentUserAccessor currentUser,
            IOptions<ServerOptions> opts,
            IDataProtectionProvider dp) =>
        {
            var body = request ?? new ConnectGraphStartRequest(null, 0, null);

            var validation = new ConnectGraphStartRequestValidator().Validate(body);
            if (!validation.IsValid)
                return Results.ValidationProblem(validation.ToDictionary());

            var options = opts.Value;
            var userId = currentUser.UserId;

            // Empty/null scope defaults to read/write (the common case for a sync destination); any
            // non-empty value must be a valid scope, which the validator already guaranteed.
            var scope = string.IsNullOrWhiteSpace(body.Scope)
                ? AccountScope.ReadWrite
                : ParseScope(body.Scope)!.Value;

            // App (cookie-less) flow: the App opens this authorizeUrl in the SYSTEM browser, which
            // never saw a server cookie. Mark the state "app" so the callback does NOT require the
            // double-submit cookie; the signed state + the loopback nonce are the integrity here.
            var authorizeUrl = BuildConnectStartUrl(
                context, dp, options, userId, scope, body.Port, body.Nonce!, "select_account", ModeApp);
            return Results.Ok(new { authorizeUrl });
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
                // App (cookie-less) flow: the App opens the returned authorizeUrl in the system
                // browser, so the callback must NOT require the double-submit cookie. The signed
                // state (which also pins the AccountId to this user) + the loopback nonce protect it.
                Mode = ModeApp,
            };
            var state = ProtectState(dp, stateModel);

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
            IGraphUserInfoService userInfo,
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

            // Double-submit CSRF — enforced ONLY for the web flow. The browser-initiated GET flow
            // does both legs in the SAME browser, so the cookie round-trips and matching it to the
            // signed state's csrf is a meaningful CSRF defense. The App flow opens the authorizeUrl
            // in the SYSTEM browser (a different cookie jar that never saw the cookie), so requiring
            // it would always 400; there the inforgeable signed state (it pins the user — the server
            // can't be tricked) plus the loopback nonce the App verifies are the integrity. Mode
            // lives INSIDE the signed blob, so it cannot be forged to downgrade a web flow to "app".
            if (string.Equals(stateModel.Mode, ModeWeb, StringComparison.Ordinal))
            {
                var cookieCsrf = context.Request.Cookies[StateCookieName];
                if (string.IsNullOrEmpty(cookieCsrf) ||
                    !string.Equals(cookieCsrf, stateModel.Csrf, StringComparison.Ordinal))
                {
                    return Results.BadRequest("Invalid OAuth state.");
                }
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
            var displayName = result.DisplayName ?? "";

            // The calendar scopes intentionally omit `openid`, so the exchange returns NO id_token
            // and therefore no email/displayName (despite the older comment claiming otherwise).
            // Capture the real mailbox + name from Graph /me using the access token we just got —
            // the User.Read scope is granted, so /me succeeds. Best-effort: if /me fails we keep
            // whatever the exchange produced (usually empty) and the listing backfill retries later.
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(displayName))
            {
                var me = await userInfo.GetMeAsync(result.AccessToken, ct);
                if (string.IsNullOrWhiteSpace(email) && me.HasEmail)
                    email = me.Email;
                if (string.IsNullOrWhiteSpace(displayName) && !string.IsNullOrWhiteSpace(me.DisplayName))
                    displayName = me.DisplayName;
            }

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

                // Opportunistically backfill the mailbox/name if the original connect missed it
                // (e.g. an account connected before /me capture existed, now upgrading scope).
                if (!string.IsNullOrWhiteSpace(email) || !string.IsNullOrWhiteSpace(displayName))
                    await accounts.UpdateProfileAsync(stateModel.AccountId, email, displayName, ct);
            }
            else
            {
                // Idempotent by email: connecting an account that is already in the pool must NOT
                // create a duplicate row. Find an existing active account for this user with the same
                // mailbox and refresh it in place (token + profile, and promote the scope when the new
                // grant is broader). Only a genuinely new mailbox inserts a row.
                var normalized = NormalizeEmail(email);
                CalendarAccount? existing = null;
                if (normalized.Length > 0)
                {
                    foreach (var a in await accounts.ListAsync(ct))
                    {
                        // Defense-in-depth: ICalendarAccountStore.ListAsync is already user-scoped
                        // (and the ambient user is pinned to stateModel.UserId above), so this only
                        // ever sees the caller's own accounts. Still match on UserId explicitly so the
                        // ownership invariant is locally evident and never silently relies on the pin.
                        if (a.UserId == stateModel.UserId &&
                            a.Kind == AccountKind.Graph &&
                            string.Equals(NormalizeEmail(a.AccountEmail), normalized, StringComparison.Ordinal))
                        {
                            existing = a;
                            break;
                        }
                    }
                }

                if (existing is not null)
                {
                    if (!string.IsNullOrEmpty(result.RefreshToken))
                        await accounts.UpdateRefreshTokenAsync(existing.Id, result.RefreshToken, ct);
                    if (!string.IsNullOrWhiteSpace(email) || !string.IsNullOrWhiteSpace(displayName))
                        await accounts.UpdateProfileAsync(existing.Id, email, displayName, ct);
                    // Promote read -> readwrite if the new consent is broader; never downgrade.
                    if (scope == AccountScope.ReadWrite && existing.Scope == AccountScope.Read)
                        await accounts.UpgradeScopeAsync(existing.Id, AccountScope.ReadWrite, ct);
                }
                else
                {
                    var account = new CalendarAccount
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        UserId = stateModel.UserId,
                        Kind = AccountKind.Graph,
                        Provider = "microsoft",
                        AccountEmail = email,
                        Authority = opts.Value.Authority,
                        Scope = scope,
                        DisplayName = string.IsNullOrWhiteSpace(displayName) ? email : displayName,
                        Status = "active",
                        ConnectedAt = DateTimeOffset.UtcNow,
                    };
                    await accounts.AddAsync(account, result.RefreshToken, ct);
                }
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
            CalendarAccountEmailBackfill backfill,
            CancellationToken ct) =>
        {
            var list = await accounts.ListAsync(ct);

            // Best-effort: backfill the real mailbox/name for any Graph account still missing it
            // (connected before /me capture existed). Already-named accounts pass through untouched.
            var enriched = new List<CalendarAccount>(list.Count);
            foreach (var a in list)
                enriched.Add(await backfill.EnsureEmailAsync(a, ct));

            return Results.Ok(enriched.Select(a => new
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
            ISyncPairStore pairs,
            ILegacyConnectedAccountAdapter adapter,
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

            // Track A-3 — DELETE every pair that references this account (source or destination)
            // before the delete, so a removed account never leaves a pair pointing at a forgotten
            // account that would later fail to resolve a token. Each pair endpoint's AccountRef may
            // be a legacy UPN or a pool accountId; resolve both sides through the adapter and
            // compare on the canonical accountId. Pairs for other accounts/users are untouched
            // (the pair store is user-scoped). The destination calendar's events are intentionally
            // left intact. The response contract stays 204 NoContent (the deleted pairs are
            // observable by their absence from GET /api/pairs).
            await DeletePairsForAccountAsync(id, pairs, adapter, ct);

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

    // Track A-3 — DELETE every pair whose source or destination resolves to the given accountId.
    // Returns the ids of the pairs that were deleted (consumed as affectedPairIds by the callers).
    // Comparison is on the canonical accountId so a legacy-UPN endpoint and a pool-accountId endpoint
    // for the same underlying account both match. The destination calendar's events are NEVER touched
    // — forget is fire-and-forget w.r.t. the destination. A deleted pair's SyncRunLock row (keyed by
    // pairId) is self-expiring (LockedUntil) and the pair never runs again, so it needs no extra
    // cleanup; the pinned-device id lived on the SyncPairRow and is removed with it.
    internal static async Task<List<string>> DeletePairsForAccountAsync(
        string accountId, ISyncPairStore pairs, ILegacyConnectedAccountAdapter adapter, CancellationToken ct)
    {
        var all = await pairs.ListAsync(ct);
        var deleted = new List<string>();

        foreach (var pair in all)
        {
            var srcId = await ResolveEndpointAccountIdAsync(pair.Source, adapter, ct);
            var dstId = await ResolveEndpointAccountIdAsync(pair.Destination, adapter, ct);

            var references =
                string.Equals(srcId, accountId, StringComparison.Ordinal) ||
                string.Equals(dstId, accountId, StringComparison.Ordinal);
            if (!references)
                continue;

            deleted.Add(pair.Id);
            await pairs.RemoveAsync(pair.Id, ct);
        }

        return deleted;
    }

    // Resolves the canonical accountId an endpoint points at. OutlookCom endpoints have no server
    // account, so they never match a pool account being deleted and resolve to null.
    private static async Task<string?> ResolveEndpointAccountIdAsync(
        Endpoint endpoint, ILegacyConnectedAccountAdapter adapter, CancellationToken ct)
    {
        if (string.Equals(endpoint.Provider, ProviderRegistry.OutlookCom, StringComparison.Ordinal))
            return null;
        return await adapter.ResolveAccountIdAsync(endpoint.AccountRef, ct);
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

    // Case/whitespace-insensitive mailbox key for dedup. Mirrors the listing's collapse-by-email so
    // a re-connect of the same casilla is treated as the same account.
    private static string NormalizeEmail(string? email) =>
        (email ?? "").Trim().ToLowerInvariant();

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

    // Shared start of a fresh (no AccountId) calendar connection: builds the signed state, sets
    // the double-submit CSRF cookie, and returns the Microsoft authorize URL. Used by both the GET
    // /calendar/connect/graph (302) and the POST /api/calendar/connect/graph/start (JSON) flows so
    // the state shape, scopes and cookie can never diverge between them (DRY).
    private static string BuildConnectStartUrl(
        HttpContext context,
        IDataProtectionProvider dp,
        ServerOptions options,
        string userId,
        AccountScope scope,
        int port,
        string nonce,
        string prompt,
        string mode)
    {
        var calendarScopes = scope == AccountScope.ReadWrite
            ? options.CalendarReadWriteScopes
            : options.CalendarReadScopes;

        var csrf = ApiKeyGenerator.Generate();
        var stateModel = new CalendarOAuthState
        {
            UserId = userId,
            Scope = scope.ToString(),
            Port = port,
            Nonce = nonce,
            Csrf = csrf,
            Mode = mode,
        };
        var state = ProtectState(dp, stateModel);

        // The cookie only matters for the web flow (same-browser double submit). It is harmless on
        // the app flow (the system browser never sends it back), but there is no reason to set it.
        if (string.Equals(mode, ModeWeb, StringComparison.Ordinal))
            AppendCsrfCookie(context, csrf);

        return BuildAuthorizeUrl(options, calendarScopes, prompt, state);
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

        // Flow mode ("web" | "app"). Travels inside the signed blob so it cannot be forged: an
        // attacker cannot downgrade a web flow to "app" to skip the CSRF cookie. Defaults to
        // "web" (the strict path) so an absent/legacy value is never treated as cookie-less.
        [System.Text.Json.Serialization.JsonPropertyName("mode")]
        public string Mode { get; set; } = ModeWeb;
    }

    private sealed class OutlookComRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("deviceId")]
        public string? DeviceId { get; set; }
    }
}

// Body for POST /api/calendar/connect/graph/start. NOTE: there is intentionally NO userId field —
// the owner is the identity-bearer token's user, never a value from the body. Scope is optional and
// defaults to read/write when empty/null; a non-empty value must be 'read' or 'readwrite'.
public sealed record ConnectGraphStartRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("scope")] string? Scope,
    [property: System.Text.Json.Serialization.JsonPropertyName("port")] int Port,
    [property: System.Text.Json.Serialization.JsonPropertyName("nonce")] string? Nonce);

public sealed class ConnectGraphStartRequestValidator : AbstractValidator<ConnectGraphStartRequest>
{
    public ConnectGraphStartRequestValidator()
    {
        // Empty/null scope is allowed (defaults to read/write). A provided value must be valid.
        RuleFor(x => x.Scope)
            .Must(s => string.IsNullOrWhiteSpace(s)
                || string.Equals(s, "read", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "readwrite", StringComparison.OrdinalIgnoreCase))
            .WithMessage("Invalid scope (expected 'read' or 'readwrite').");

        RuleFor(x => x.Port)
            .InclusiveBetween(1024, 65535)
            .WithMessage("Invalid loopback port.");

        RuleFor(x => x.Nonce)
            .NotEmpty()
            .WithMessage("Missing nonce.");
    }
}
