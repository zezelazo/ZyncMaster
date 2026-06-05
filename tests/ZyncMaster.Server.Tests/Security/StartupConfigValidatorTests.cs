using System;
using FluentAssertions;
using ZyncMaster.Server.Configuration;
using Xunit;

namespace ZyncMaster.Server.Tests.Security;

// Unit tests for the production startup fail-fast that guards the OAuth / magic-link critical
// config. Mirrors the connection-string fail-fast already in Program.cs: in any non-Development
// environment, empty MicrosoftClientId / IdentityRedirectUri / CalendarRedirectUri / PublicBaseUrl
// must abort host build with a clear error rather than serving a broken sign-in flow. In
// Development (the test/dev environment) the validator is a no-op so the suite keeps running
// with empty config.
public class StartupConfigValidatorTests
{
    // A non-empty secret stands in for what Program.cs reads from Microsoft:ClientSecret
    // (user-secrets/env vars). The validator treats a blank/whitespace secret as fatal in production.
    private const string Secret = "the-client-secret";

    private static ServerOptions FullyConfigured() => new()
    {
        MicrosoftClientId = "cid",
        IdentityRedirectUri = "https://app.example.com/identity/connect/callback/microsoft",
        CalendarRedirectUri = "https://app.example.com/calendar/connect/callback/graph",
        PublicBaseUrl = "https://app.example.com",
    };

    [Fact]
    public void Development_with_empty_config_does_not_throw()
    {
        var act = () => StartupConfigValidator.ValidateOAuthConfig(
            new ServerOptions(), isDevelopment: true, microsoftClientSecret: null);
        act.Should().NotThrow();
    }

    [Fact]
    public void Production_with_full_config_does_not_throw()
    {
        var act = () => StartupConfigValidator.ValidateOAuthConfig(
            FullyConfigured(), isDevelopment: false, microsoftClientSecret: Secret);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("MicrosoftClientId")]
    [InlineData("IdentityRedirectUri")]
    [InlineData("CalendarRedirectUri")]
    [InlineData("PublicBaseUrl")]
    public void Production_with_one_missing_critical_value_throws_naming_the_setting(string missing)
    {
        var opts = FullyConfigured();
        switch (missing)
        {
            case "MicrosoftClientId": opts.MicrosoftClientId = "  "; break;
            case "IdentityRedirectUri": opts.IdentityRedirectUri = ""; break;
            case "CalendarRedirectUri": opts.CalendarRedirectUri = ""; break;
            case "PublicBaseUrl": opts.PublicBaseUrl = ""; break;
        }

        var act = () => StartupConfigValidator.ValidateOAuthConfig(
            opts, isDevelopment: false, microsoftClientSecret: Secret);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{missing}*");
    }

    // FIX C — a blank Microsoft:ClientSecret in production must fail fast and name the secret in the
    // 'missing' list. Without this guard ConfigurationSecretProvider silently returns "" and EVERY
    // OAuth token exchange fails at runtime with an opaque AADSTS error.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Production_with_missing_client_secret_throws_naming_the_secret(string? secret)
    {
        var act = () => StartupConfigValidator.ValidateOAuthConfig(
            FullyConfigured(), isDevelopment: false, microsoftClientSecret: secret);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Microsoft:ClientSecret*");
    }

    [Fact]
    public void Development_with_missing_client_secret_does_not_throw()
    {
        // Development tolerates an absent secret (the OAuth flows are not exercised end-to-end).
        var act = () => StartupConfigValidator.ValidateOAuthConfig(
            FullyConfigured(), isDevelopment: true, microsoftClientSecret: null);
        act.Should().NotThrow();
    }

    [Fact]
    public void Production_with_all_empty_lists_every_missing_setting()
    {
        var act = () => StartupConfigValidator.ValidateOAuthConfig(
            new ServerOptions(), isDevelopment: false, microsoftClientSecret: null);

        act.Should().Throw<InvalidOperationException>()
            .Where(e => e.Message.Contains("MicrosoftClientId")
                && e.Message.Contains("Microsoft:ClientSecret")
                && e.Message.Contains("IdentityRedirectUri")
                && e.Message.Contains("CalendarRedirectUri")
                && e.Message.Contains("PublicBaseUrl"));
    }
}
