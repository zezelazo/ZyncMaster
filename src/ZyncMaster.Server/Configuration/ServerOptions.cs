namespace ZyncMaster.Server;

public sealed class ServerOptions
{
    public string MicrosoftClientId { get; set; } = "";
    public string Authority { get; set; } = "https://login.microsoftonline.com/common/oauth2/v2.0";
    public string RedirectUri { get; set; } = "";
    public string Scopes { get; set; } = "offline_access Calendars.ReadWrite User.Read";

    // Identity (sign-in) OAuth scopes. Distinct from calendar Scopes above: this flow only
    // proves who the user is (openid email profile), it NEVER requests calendar access nor
    // stores a calendar refresh token. Connecting a calendar is a separate, later flow.
    public string IdentityScopes { get; set; } = "openid email profile";

    // Redirect URI for the identity OAuth callback. Points at
    // /identity/connect/callback/microsoft. Empty by default; set per-environment.
    public string IdentityRedirectUri { get; set; } = "";

    // Calendar-connect OAuth scopes (Track A-2). Distinct from the legacy single-user `Scopes`
    // and from `IdentityScopes`: connecting a calendar account to the per-user pool requests
    // exactly the calendar grant the user picked — read-only or read/write — plus offline_access
    // so a refresh token is returned. `User.Read` is included so the id_token carries the
    // account email we record on the CalendarAccount.
    public string CalendarReadScopes { get; set; } = "offline_access Calendars.Read User.Read";
    public string CalendarReadWriteScopes { get; set; } = "offline_access Calendars.ReadWrite User.Read";

    // Redirect URI for the calendar-connect OAuth callback. Points at
    // /calendar/connect/callback/graph. Empty by default; set per-environment.
    public string CalendarRedirectUri { get; set; } = "";

    public int SyncWindowDays { get; set; } = 14;

    // Per-pair run-lock TTL (plan v2 §B-1). A run acquires the lock for this long; the lock
    // is released in finally, but the TTL bounds how long a crashed holder can wedge the
    // pair before another executor may re-acquire. There is no mid-run renewal (see
    // ISyncRunLock), so this TTL MUST exceed the worst-case duration of a single mirror —
    // otherwise the lock could lapse mid-run and a second executor could start a concurrent
    // destructive sweep. The default 8 min comfortably exceeds the cost of one mirror over the
    // default 14-day / $top=50 window; widen it if the window or page size grows materially.
    public int SyncRunLockTtlMinutes { get; set; } = 8;
    public string ExtendedPropertyGuid { get; set; } = "6f0e7f2c-3b1a-4e8d-9c2f-7a5b1d9e4c30";

    // Identity access token (internal Server<->App bearer) lifetime. Short by design: the
    // token is a master key that registers devices and connects calendars, so it is renewed
    // via the long-lived refresh token rather than being long-lived itself (plan v2 §A-1).
    public int IdentityAccessTokenTtlMinutes { get; set; } = 1440; // 24h

    // Identity refresh token lifetime. Long-lived, opaque, stored hashed; exchanged for a
    // fresh access token at App start / on near-expiry.
    public int IdentityRefreshTokenTtlDays { get; set; } = 90;

    // Magic-link (passwordless local login) token lifetime. Short by design: the link in the
    // email is a one-time bearer of login, so it expires fast (plan deferred §4 = 15m).
    public int MagicLinkTtlMinutes { get; set; } = 15;

    // Public base URL used to build the magic-link the user clicks (e.g. https://app.example.com).
    // The link points at {PublicBaseUrl}/identity/magic-link/callback?token=... . Empty by default;
    // set per-environment. When empty, the link is built from the incoming request's scheme+host
    // so the dev/test flow still works without configuration.
    public string PublicBaseUrl { get; set; } = "";

    // Per-email rate-limit window for magic-link requests (plan A-6). Within
    // MagicLinkRateLimitWindowMinutes, at most MagicLinkMaxPerEmail links are actually sent for a
    // given email; further requests still return a constant 202 but send nothing (silent —
    // preserves constant timing and anti-enumeration). Distinct from the per-IP ASP.NET rate
    // limiter, which guards against raw endpoint abuse and may return 429.
    public int MagicLinkMaxPerEmail { get; set; } = 3;
    public int MagicLinkRateLimitWindowMinutes { get; set; } = 15;

    // Per-IP ASP.NET rate limiter for the magic-link POST (plan A-6). A fixed window allowing
    // MagicLinkMaxPerIp requests per MagicLinkRateLimitWindowMinutes from one client address;
    // excess is rejected with 429. This is anti-abuse, not anti-enumeration (it does not depend
    // on whether the email exists), so it does not leak user existence.
    public int MagicLinkMaxPerIp { get; set; } = 20;

    // Device lease TTL (Track B Phase 3). On register and on every heartbeat the device's
    // LeaseUntil is set to now + this many minutes. While LeaseUntil > now the App is treated as
    // running that device's syncs, so the server-side cron trigger (/api/sync/run-due) skips the
    // owning user's pairs to avoid a double run. The App heartbeats well within this window
    // (default 10m) so a brief network hiccup does not immediately hand the user's syncs to cron.
    public int DeviceLeaseTtlMinutes { get; set; } = 10;

    // Shared secret that authenticates the EXTERNAL cron trigger calling POST /api/sync/run-due
    // (plan §D-1/§D-2 — the cron-trigger model REPLACES the Azure Functions timer; no AlwaysOn).
    // The caller presents it as "X-Cron-Secret: <secret>" or "Authorization: Bearer <secret>"; it
    // is compared in CONSTANT time. This is NOT a device api key nor a user bearer — it is the
    // scheduler's key, so the endpoint is gated by this secret alone, never RequireApiKey/Bearer.
    // Empty by default: when unset the endpoint is DISABLED (503) rather than open, so a
    // misconfigured deployment never exposes an unauthenticated server-side run trigger. Set it
    // per-environment via "Server:CronTriggerSecret" (user-secrets in dev, env var in prod).
    public string CronTriggerSecret { get; set; } = "";

    // Interval (hours) between ephemeral-table purge sweeps (§A/§D hygiene). The background
    // EphemeralPurgeService set-deletes expired identity access/refresh tokens, expired/consumed
    // magic-links, expired run-locks AND expired pending pairings every this-many hours.
    // Low-frequency by design: these rows are tiny and self-expiring, so 6h keeps the tables tidy
    // without adding write pressure.
    public int EphemeralPurgeIntervalHours { get; set; } = 6;

    // Device-pairing code TTL (FIX A). A PendingPairing whose CreatedUtc + this-many minutes has
    // passed is treated as expired: GetPendingByCodeAsync no longer resolves it (so an expired code
    // can neither be viewed at /pair nor approved) and the ephemeral purge deletes it. Short by
    // design — the human approves within a couple of minutes of the device showing the code — but
    // generous enough for a distracted user. The same TTL bounds the brute-force window: combined
    // with the increased code entropy and the per-IP rate limiter, an attacker has only this long
    // to guess a live code.
    public int PendingPairingTtlMinutes { get; set; } = 15;

    // Per-IP fixed-window rate limit for the pairing endpoints (FIX A) — /api/devices/approve,
    // /api/pair/start and /api/pair/complete. Caps raw guessing/abuse from one client address per
    // PairingRateLimitWindowMinutes; excess is rejected with 429. This is the brute-force defense
    // for the wider pairing code: even with the larger alphabet an unthrottled attacker could grind
    // codes, so the limiter bounds attempts per window. Anti-abuse only (does not branch on whether
    // a code exists), so it leaks nothing.
    public int PairingMaxPerIp { get; set; } = 20;
    public int PairingRateLimitWindowMinutes { get; set; } = 15;
}
