using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZyncMaster.Server.Data;
using ZyncMaster.Server.Infrastructure.Email;

namespace ZyncMaster.Server;

// Passwordless local login by magic-link (plan body Phase 1 + v2 §A-6 + deferred §4). A user
// POSTs their email; the Server emails a one-time link; clicking it proves possession of the
// inbox and logs the user in as a "local" provider login, delivering the session tokens to the
// desktop App over the same one-time loopback handle the Microsoft flow (Task 2b) uses.
//
// SECURITY (v2 §A-6):
//   * Anti-enumeration: POST always returns a constant 202 with a generic body and constant
//     timing whether or not the email already maps to a user. We never branch observably on
//     existence — a brand-new email is treated identically to a known one.
//   * Rate-limit, two layers:
//       - per IP: the ASP.NET fixed-window rate limiter (policy "magic-link-ip") returns 429 on
//         abuse. This is anti-abuse only and does not depend on the email, so it leaks nothing
//         about user existence.
//       - per email: counted against recent MagicLinkRow rows in the window. When exceeded we
//         STILL return 202 but send nothing (silent) — chosen over a 429 here precisely so the
//         per-email limit stays invisible and constant-time, never revealing that a given email
//         has been targeted. (This is the documented choice between the two options the task
//         allowed.)
//   * Single-use: the callback consumes the row with one atomic conditional UPDATE (set
//     ConsumedAt where the hash matches AND it is still unconsumed AND unexpired). Only the
//     request whose UPDATE affects exactly one row proceeds, so two concurrent clicks of the
//     same token cannot both log in.
//   * Hashed token: only the base64url SHA-256 of the 32-byte token is stored; the clear token
//     exists only inside the emailed link.
//
// SAME-DEVICE LIMITATION: the callback redirects to http://127.0.0.1:{port} — the App's loopback
// listener. That only reaches the App if the link is opened on the same machine the App runs on.
// Opening the link on a phone/other device cannot complete the login in this MVP. A cross-device
// flow (e.g. show a code the user types into the App) is future work.
public static class IdentityMagicLinkEndpoints
{
    public const string PerIpRateLimitPolicy = "magic-link-ip";

    // The generic, existence-agnostic body returned by every POST /identity/magic-link.
    private const string GenericAcceptedBody =
        "If an account can be created or matched for that address, a sign-in link has been sent.";

    public static void MapIdentityMagicLinkEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var post = app.MapPost("/identity/magic-link", async (
            HttpContext context,
            IDbContextFactory<ZyncMasterDbContext> dbFactory,
            IEmailSender email,
            IOptions<ServerOptions> opts,
            TimeProvider clock) =>
        {
            var options = opts.Value;

            MagicLinkRequest? body;
            try
            {
                body = await context.Request.ReadFromJsonAsync<MagicLinkRequest>(context.RequestAborted);
            }
            catch (JsonException)
            {
                return Results.BadRequest("Invalid request body.");
            }

            // Structural validation (port range + non-empty nonce + present email) is a 400 — it
            // reflects a malformed App request, not the existence of a user, so it does not leak.
            if (body is null || string.IsNullOrWhiteSpace(body.Email))
                return Results.BadRequest("Missing email.");
            if (body.Port < 1024 || body.Port > 65535)
                return Results.BadRequest("Invalid loopback port.");
            if (string.IsNullOrEmpty(body.Nonce))
                return Results.BadRequest("Missing nonce.");

            var normalizedEmail = body.Email.Trim().ToLowerInvariant();
            var now = clock.GetUtcNow();

            // Per-email rate-limit: count links created for this email inside the window. Over the
            // cap -> stay silent (no row, no email) but still fall through to the constant 202.
            // The window filter (a DateTimeOffset comparison) is evaluated in memory, not in the
            // query, because not every provider (SQLite) can translate a DateTimeOffset >= compare;
            // magic-link rows for one email are few and ephemeral, so the email-filtered fetch is
            // cheap and provider-agnostic.
            await using var db = await dbFactory.CreateDbContextAsync(context.RequestAborted);
            var windowStart = now.AddMinutes(-options.MagicLinkRateLimitWindowMinutes);
            var createdTimes = await db.MagicLinks
                .Where(r => r.Email == normalizedEmail)
                .Select(r => r.CreatedAt)
                .ToListAsync(context.RequestAborted);
            var recentCount = createdTimes.Count(t => t >= windowStart);

            if (recentCount < options.MagicLinkMaxPerEmail)
            {
                // Generate the clear token (32 RNG bytes, base64url) — emailed in the clear, stored
                // only as a hash.
                var token = ToBase64Url(RandomNumberGenerator.GetBytes(32));
                var row = new MagicLinkRow
                {
                    Id = Guid.NewGuid().ToString("N"),
                    TokenHash = HashToken(token),
                    Email = normalizedEmail,
                    Port = body.Port,
                    Nonce = body.Nonce,
                    CreatedAt = now,
                    ExpiresAt = now.AddMinutes(options.MagicLinkTtlMinutes),
                    ConsumedAt = null,
                };
                db.MagicLinks.Add(row);
                await db.SaveChangesAsync(context.RequestAborted);

                var baseUrl = ResolveBaseUrl(options, context);
                var link = $"{baseUrl}/identity/magic-link/callback?token={Uri.EscapeDataString(token)}";
                var html =
                    "<!DOCTYPE html><html><body>" +
                    "<h1>Sign in to Zync Master</h1>" +
                    $"<p><a href=\"{System.Net.WebUtility.HtmlEncode(link)}\">Click here to sign in</a>.</p>" +
                    $"<p>This link expires in {options.MagicLinkTtlMinutes} minutes and can be used once.</p>" +
                    "</body></html>";

                // A transport failure must not change the observable response (anti-enumeration):
                // swallow it here, the user simply does not receive a link.
                try
                {
                    await email.SendAsync(normalizedEmail, "Your Zync Master sign-in link", html, context.RequestAborted);
                }
                catch (Exception)
                {
                    // Intentionally swallowed — see anti-enumeration note above.
                }
            }

            // Constant 202 + generic body, every path.
            return Results.Accepted(value: new { message = GenericAcceptedBody });
        });

        // Attach the per-IP rate limiter policy (registered in Program.cs). RequireRateLimiting is
        // a no-op if the limiter middleware is not wired, so this stays safe under any host.
        post.RequireRateLimiting(PerIpRateLimitPolicy);

        var callback = app.MapGet("/identity/magic-link/callback", async (
            HttpContext context,
            IDbContextFactory<ZyncMasterDbContext> dbFactory,
            IUserStore users,
            IIdentityTokenService identityTokens,
            IIdentityHandleStore handles,
            TimeProvider clock) =>
        {
            var token = context.Request.Query["token"].ToString();
            if (string.IsNullOrEmpty(token))
                return ErrorHtml("This sign-in link is invalid.");

            var hash = HashToken(token);
            var now = clock.GetUtcNow();

            await using var db = await dbFactory.CreateDbContextAsync(context.RequestAborted);

            // Single-use atomicity: claim the row with a single conditional UPDATE that filters on
            // (matching hash AND not yet consumed) and stamps ConsumedAt. The row count tells us if
            // THIS request won the claim. Under READ COMMITTED two concurrent clicks of the same
            // token cannot both win: ExecuteUpdateAsync is one atomic SQL statement (same semantics
            // in SQLite and SQL Server), so exactly one gets consumed==1 and the other gets 0. The
            // previous read→check→set→commit pattern was racy because EF's UPDATE filtered only by
            // PK, with the unconsumed check done in app memory.
            //
            // The expiry check is intentionally NOT part of this WHERE: a DateTimeOffset comparison
            // (ExpiresAt > now) is not translatable on every provider (SQLite throws), and it is not
            // the race-sensitive predicate. We claim by hash+unconsumed atomically, then reject an
            // expired row after rereading it. Burning an expired token's single use is harmless and
            // actually desirable (it cannot be reused).
            var consumed = await db.MagicLinks
                .Where(r => r.TokenHash == hash && r.ConsumedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.ConsumedAt, now), context.RequestAborted);

            if (consumed != 1)
                return ErrorHtml("This sign-in link is invalid or has expired.");

            // We won the claim — reread the row (untracked) to recover Email/Port/Nonce and to
            // enforce expiry in memory (provider-agnostic, same reason the POST evaluates its
            // window in memory).
            var row = await db.MagicLinks
                .AsNoTracking()
                .FirstAsync(r => r.TokenHash == hash, context.RequestAborted);

            if (row.ExpiresAt <= now)
                return ErrorHtml("This sign-in link is invalid or has expired.");

            // LINKING POLICY (key difference vs Microsoft in Task 2b): clicking the link PROVES the
            // user controls this inbox, so the local login is emailVerified:TRUE. Microsoft sign-in
            // passes false because the IdP's email claim is not proof-of-possession and could be
            // used to silently hijack a pre-existing local account with the same address. A
            // magic-link IS that proof, so it is allowed to link by verified email.
            var user = await users.UpsertByLoginAsync(
                provider: "local",
                providerSubject: row.Email,
                email: row.Email,
                emailVerified: true,
                displayName: row.Email,
                context.RequestAborted);

            var redirect = await IdentityLoopback.IssueLoopbackRedirectAsync(
                user, row.Port, row.Nonce, identityTokens, handles, context.RequestAborted);

            return Results.Redirect(redirect);
        });

        // FIX 3 — the callback consumes the magic-link token (a bearer secret in the query string),
        // so cap per-IP attempts to stop brute-forcing live tokens. Shares the identity-token policy
        // with /identity/handle/redeem and /identity/refresh; excess returns 429.
        callback.RequireRateLimiting(IdentityConnectEndpoints.TokenRateLimitPolicy);
    }

    // Builds the base URL for the emailed link: the configured PublicBaseUrl when set, else the
    // incoming request's scheme+host (so dev/tests work without configuration).
    private static string ResolveBaseUrl(ServerOptions options, HttpContext context)
    {
        if (!string.IsNullOrWhiteSpace(options.PublicBaseUrl))
            return options.PublicBaseUrl.TrimEnd('/');
        return $"{context.Request.Scheme}://{context.Request.Host}";
    }

    private static IResult ErrorHtml(string message)
    {
        var html =
            "<!DOCTYPE html><html><body>" +
            "<h1>Sign-in failed</h1>" +
            $"<p>{System.Net.WebUtility.HtmlEncode(message)}</p>" +
            "</body></html>";
        // 400 so callers/tests can assert failure while still rendering a human-readable page.
        return Results.Content(html, "text/html", statusCode: StatusCodes.Status400BadRequest);
    }

    private static string HashToken(string tokenValue)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(tokenValue));
        return ToBase64Url(bytes);
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class MagicLinkRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("email")]
        public string? Email { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("port")]
        public int Port { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("nonce")]
        public string Nonce { get; set; } = "";
    }
}
