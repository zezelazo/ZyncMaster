namespace ZyncMaster.Server.Configuration;

// Startup fail-fast for the OAuth / magic-link critical configuration. Same discipline as the
// connection-string guard in Program.cs: in any non-Development environment a missing critical
// value aborts host build with one clear, aggregated error rather than serving a broken sign-in
// flow (a blank MicrosoftClientId / redirect URI would produce opaque "AADSTS900..." failures at
// the OAuth provider; a blank PublicBaseUrl would mint magic-links pointing at the wrong host).
//
// Gated on !IsDevelopment() by the caller so the WebApplicationFactory test host and local dev
// (both Development, with empty config) keep starting. Mailjet is deliberately NOT validated here
// — it is optional and falls back to LoggingEmailSender when unconfigured.
public static class StartupConfigValidator
{
    // Validates the OAuth / magic-link critical config at startup. `microsoftClientSecret` is the
    // value resolved from the secret store (Microsoft:ClientSecret) — passed in rather than read
    // off ServerOptions because the secret deliberately does NOT live in the 'Server' config
    // section (it stays in user-secrets/env vars). A blank secret is just as fatal as a blank
    // ClientId: every OAuth token exchange would fail at runtime with an opaque AADSTS error —
    // exactly the failure mode this fail-fast exists to prevent — so it is validated here too.
    public static void ValidateOAuthConfig(
        ServerOptions options, bool isDevelopment, string? microsoftClientSecret = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Development (and the test host) tolerate empty config: the magic-link flow builds the
        // link from the incoming request host, and the OAuth flows are not exercised end-to-end.
        if (isDevelopment)
            return;

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(options.MicrosoftClientId)) missing.Add("MicrosoftClientId");
        if (string.IsNullOrWhiteSpace(microsoftClientSecret)) missing.Add("Microsoft:ClientSecret");
        if (string.IsNullOrWhiteSpace(options.IdentityRedirectUri)) missing.Add("IdentityRedirectUri");
        if (string.IsNullOrWhiteSpace(options.CalendarRedirectUri)) missing.Add("CalendarRedirectUri");
        if (string.IsNullOrWhiteSpace(options.PublicBaseUrl)) missing.Add("PublicBaseUrl");

        // FIX 4 — the cron trigger runs DESTRUCTIVE cross-user syncs gated ONLY by CronTriggerSecret.
        // A blank secret silently DISABLES the trigger (so cron never runs and nothing syncs when no
        // App is up — a "no sync" footgun), and a short/low-entropy secret is brute-forceable. In a
        // non-Development environment require it present and >= 32 chars so a real deployment cannot
        // ship a weak or accidentally-empty trigger secret.
        const int minCronSecretLength = 32;
        if (string.IsNullOrWhiteSpace(options.CronTriggerSecret))
            missing.Add("CronTriggerSecret (required, >= 32 chars)");
        else if (options.CronTriggerSecret.Trim().Length < minCronSecretLength)
            missing.Add($"CronTriggerSecret (too short: needs >= {minCronSecretLength} chars)");

        // FIX 5 — a localhost / 127.0.0.1 redirect or public base URL is a dev-only default; shipping
        // it to a real environment would mint OAuth callbacks / magic-links pointing at the operator's
        // own machine. Reject any loopback host in the externally-reachable URLs outside Development.
        if (IsLoopbackUrl(options.IdentityRedirectUri)) missing.Add("IdentityRedirectUri (must not be localhost)");
        if (IsLoopbackUrl(options.CalendarRedirectUri)) missing.Add("CalendarRedirectUri (must not be localhost)");
        if (IsLoopbackUrl(options.RedirectUri)) missing.Add("RedirectUri (must not be localhost)");
        if (IsLoopbackUrl(options.PublicBaseUrl)) missing.Add("PublicBaseUrl (must not be localhost)");

        if (missing.Count == 0)
            return;

        throw new InvalidOperationException(
            "Missing or invalid required configuration in a non-Development environment: " +
            string.Join(", ", missing) + ". " +
            "Set the 'Server'-section values per-environment (e.g. the app settings " +
            "Server__MicrosoftClientId, Server__IdentityRedirectUri, Server__CalendarRedirectUri, " +
            "Server__PublicBaseUrl, Server__CronTriggerSecret). The secret Microsoft:ClientSecret " +
            "stays in user-secrets/env vars (set it via 'Microsoft__ClientSecret'), NOT in the " +
            "committed 'Server' section.");
    }

    // True when the URL is non-empty and points at a loopback host (localhost / 127.x / [::1]). A
    // value that does not parse as an absolute URI is treated as NOT loopback here — the blank/missing
    // checks above already cover the empty case, and a malformed non-empty value is surfaced by the
    // OAuth provider rather than this guard.
    private static bool IsLoopbackUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return false;
        return uri.IsLoopback
            || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    }
}
