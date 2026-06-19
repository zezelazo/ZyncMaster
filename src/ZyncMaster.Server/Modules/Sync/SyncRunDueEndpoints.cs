using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace ZyncMaster.Server;

// The external cron-trigger endpoint (plan §D-1/§D-2). An external scheduler (VPS cron, etc.)
// POSTs here on its own cadence and the server runs every due, uncovered pair server-side — no
// Azure AlwaysOn / Functions timer.
//
// Auth: a single shared service secret (ServerOptions.CronTriggerSecret), presented as
// "X-Cron-Secret: <secret>" or "Authorization: Bearer <secret>", compared in CONSTANT time. This
// is deliberately NOT RequireApiKey/RequireIdentityBearer: the caller is the scheduler, not a
// device or a user. When the secret is unset the endpoint is DISABLED (503) rather than open.
public static class SyncRunDueEndpoints
{
    public const string SecretHeader = "X-Cron-Secret";
    private const string BearerPrefix = "Bearer ";

    // FIX 4 — per-IP fixed-window rate-limit policy for the destructive cron trigger. Registered in
    // Program.cs and attached via RequireRateLimiting below; excess returns 429 even with a valid
    // secret (defense-in-depth against a leaked secret + endpoint abuse).
    public const string RateLimitPolicy = "cron-run-due-ip";

    public static void MapSyncRunDueEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/sync/run-due", async (
            HttpContext http,
            IOptions<ServerOptions> opts,
            CronSyncRunner runner,
            ReplicaSyncRunner replicaRunner,
            CancellationToken ct) =>
        {
            var configured = opts.Value.CronTriggerSecret;
            if (string.IsNullOrEmpty(configured))
            {
                // No secret configured -> the trigger is disabled, never open. 503 so a probe can
                // tell "not enabled here" apart from "wrong secret" (401).
                return Results.Json(
                    new { error = "cron_trigger_disabled", message = "Cron trigger is not configured." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var presented = ExtractSecret(http.Request);
            if (presented is null || !ConstantTimeEquals(presented, configured))
                return Results.Unauthorized();

            // FIX 5 — log a one-line per-run summary at Info so a "it doesn't sync" report can be
            // diagnosed from the logs alone (how many pairs were due vs run vs skipped vs failed).
            var summary = await runner.RunDueAsync(ct);

            // Calendar v2: the SAME external trigger drives the replica engine + prefix rules
            // (spec §11 — the VPS crontab is the only scheduler). Additive response key.
            var calendar = await replicaRunner.RunAsync(ct);

            // The endpoint returns 200 even when individual pairs failed (it RAN fine; the failures are
            // per-pair data, and the cron caller must not retry the whole batch on one bad pair). The
            // top-level `hadFailures` flag is the monitor-friendly signal: a dead-man's switch / uptime
            // check can branch on it without parsing every nested count. The per-failure detail is
            // logged at Warning by the runners (CronSyncRunner / ReplicaSyncRunner).
            var hadFailures = summary.Failed > 0 || calendar.Failed > 0;

            return Results.Ok(new
            {
                hadFailures,
                ran = summary.Ran,
                skipped = summary.Skipped,
                failed = summary.Failed,
                calendar = new
                {
                    usersProcessed = calendar.UsersProcessed,
                    rulesApplied = calendar.RulesApplied,
                    replicasCreated = calendar.ReplicasCreated,
                    moved = calendar.Moved,
                    cancelled = calendar.Cancelled,
                    broken = calendar.Broken,
                    failed = calendar.Failed,
                },
            });
        }).RequireRateLimiting(RateLimitPolicy);
    }

    // Reads the secret from X-Cron-Secret, else from a Bearer Authorization header. Null when
    // neither carries a non-empty value.
    private static string? ExtractSecret(HttpRequest request)
    {
        if (request.Headers.TryGetValue(SecretHeader, out var header))
        {
            var value = header.ToString();
            if (!string.IsNullOrEmpty(value))
                return value;
        }

        if (request.Headers.TryGetValue("Authorization", out var auth))
        {
            var raw = auth.ToString();
            if (raw.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var token = raw[BearerPrefix.Length..].Trim();
                if (!string.IsNullOrEmpty(token))
                    return token;
            }
        }

        return null;
    }

    // Constant-time comparison so a timing side-channel cannot reveal the secret one byte at a
    // time. FixedTimeEquals already runs in time independent of WHERE the mismatch is; length is
    // compared first (lengths are not themselves the secret).
    private static bool ConstantTimeEquals(string a, string b)
    {
        var ab = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(ab, bb);
    }
}
