using Microsoft.AspNetCore.Authentication;

namespace ZyncMaster.Server;

public static class ApiKeyAuthExtensions
{
    public static AuthenticationBuilder AddApiKeyAuth(this AuthenticationBuilder builder) =>
        builder.AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
            ApiKeyAuthenticationHandler.SchemeName, null);
}
