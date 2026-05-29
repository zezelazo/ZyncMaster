using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;

namespace ZyncMaster.Server;

// Central definition of the two authentication schemes and the per-scheme authorization
// requirements. Device traffic (push / run / device management + sync) is gated by the
// ApiKey scheme; the human panel (status, accounts, pair management, /api/me) is gated by
// the Cookie scheme. Endpoints opt in explicitly via RequireApiKey() / RequireCookie() so
// each route is unambiguously bound to the right scheme.
public static class AuthSchemes
{
    public const string Cookie = "Cookie";
    public const string ApiKey = ApiKeyAuthenticationHandler.SchemeName;

    // Builds the authenticated panel principal carrying the ZyncMaster user id claim that
    // HttpContextCurrentUserAccessor resolves on later requests.
    public static ClaimsPrincipal BuildCookiePrincipal(string userId, string? email, string? displayName)
    {
        var claims = new List<Claim>
        {
            new(HttpContextCurrentUserAccessor.UserIdClaimType, userId),
            new(ClaimTypes.NameIdentifier, userId),
        };
        if (!string.IsNullOrEmpty(email))
            claims.Add(new Claim(ClaimTypes.Email, email));
        if (!string.IsNullOrEmpty(displayName))
            claims.Add(new Claim(ClaimTypes.Name, displayName));

        var identity = new ClaimsIdentity(claims, Cookie);
        return new ClaimsPrincipal(identity);
    }

    public static TBuilder RequireApiKey<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        (TBuilder)builder.RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = ApiKey });

    public static TBuilder RequireCookie<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        (TBuilder)builder.RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = Cookie });
}
