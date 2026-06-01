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
    public static void ValidateOAuthConfig(ServerOptions options, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Development (and the test host) tolerate empty config: the magic-link flow builds the
        // link from the incoming request host, and the OAuth flows are not exercised end-to-end.
        if (isDevelopment)
            return;

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(options.MicrosoftClientId)) missing.Add("MicrosoftClientId");
        if (string.IsNullOrWhiteSpace(options.IdentityRedirectUri)) missing.Add("IdentityRedirectUri");
        if (string.IsNullOrWhiteSpace(options.CalendarRedirectUri)) missing.Add("CalendarRedirectUri");
        if (string.IsNullOrWhiteSpace(options.PublicBaseUrl)) missing.Add("PublicBaseUrl");

        if (missing.Count == 0)
            return;

        throw new InvalidOperationException(
            "Missing required OAuth / magic-link configuration in a non-Development environment: " +
            string.Join(", ", missing) + ". " +
            "Set them per-environment under the 'Server' section (e.g. the app settings " +
            "Server__MicrosoftClientId, Server__IdentityRedirectUri, Server__CalendarRedirectUri, " +
            "Server__PublicBaseUrl). Secrets such as Microsoft:ClientSecret stay in user-secrets/env vars.");
    }
}
