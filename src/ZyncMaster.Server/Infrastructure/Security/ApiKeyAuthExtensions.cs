using Microsoft.AspNetCore.Authentication;

namespace ZyncMaster.Server;

public static class ApiKeyAuthExtensions
{
    public static AuthenticationBuilder AddApiKeyAuth(this AuthenticationBuilder builder) =>
        builder.AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
            ApiKeyAuthenticationHandler.SchemeName, null);

    // Registers the IdentityBearer scheme (validates "Authorization: Bearer <identity access
    // token>"), alongside the ApiKey and Cookie schemes.
    public static AuthenticationBuilder AddIdentityBearerAuth(this AuthenticationBuilder builder) =>
        builder.AddScheme<AuthenticationSchemeOptions, IdentityBearerAuthenticationHandler>(
            IdentityBearerAuthenticationHandler.SchemeName, null);
}
