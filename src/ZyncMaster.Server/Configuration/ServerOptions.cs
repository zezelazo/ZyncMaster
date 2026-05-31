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
    public int SyncWindowDays { get; set; } = 14;
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
}
