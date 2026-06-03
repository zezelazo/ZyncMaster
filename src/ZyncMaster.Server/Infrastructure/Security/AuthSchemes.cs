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

    // Bearer scheme backed by identity access tokens (Track A-2 calendar-account endpoints and
    // Track B device registration). Validated by IdentityBearerAuthenticationHandler.
    public const string IdentityBearer = IdentityBearerAuthenticationHandler.SchemeName;

    // Builds the authenticated panel principal carrying the ZyncMaster user id claim that
    // HttpContextCurrentUserAccessor resolves on later requests.
    public static ClaimsPrincipal BuildCookiePrincipal(string userId, string? email, string? displayName) =>
        BuildPrincipal(userId, email, displayName, Cookie);

    // Builds the principal for an IdentityBearer-authenticated request. Stamps the SAME userId
    // claim as the cookie/api-key schemes (HttpContextCurrentUserAccessor.UserIdClaimType) so the
    // accessor resolves the caller identically regardless of how they authenticated.
    public static ClaimsPrincipal BuildIdentityBearerPrincipal(string userId, string? email, string? displayName) =>
        BuildPrincipal(userId, email, displayName, IdentityBearer);

    // Shared principal builder: the userId claim is the single load-bearing claim for
    // ICurrentUserAccessor; email/displayName are carried for convenience. The authenticationType
    // becomes the identity's AuthenticationType (the scheme that authenticated the request).
    private static ClaimsPrincipal BuildPrincipal(
        string userId, string? email, string? displayName, string authenticationType)
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

        var identity = new ClaimsIdentity(claims, authenticationType);
        return new ClaimsPrincipal(identity);
    }

    public static TBuilder RequireApiKey<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        (TBuilder)builder.RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = ApiKey });

    public static TBuilder RequireCookie<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        (TBuilder)builder.RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = Cookie });

    // Requires a valid identity bearer token on the endpoint(s). Used by /api/identity/me and the
    // upcoming calendar-account / device-registration endpoints.
    public static TBuilder RequireIdentityBearer<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        (TBuilder)builder.RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = IdentityBearer });

    // Accepts EITHER scheme. The browser panel authenticates with the Cookie scheme while
    // a device authenticates with the ApiKey scheme; both set the "userId" claim that
    // HttpContextCurrentUserAccessor resolves, and the handlers load data user-scoped, so a
    // route exposed to both schemes is safe. Used by /api/pairs/{id}/run, which the panel's
    // per-pair "Sync now" button calls under the cookie while devices call it under the key.
    public static TBuilder RequireCookieOrApiKey<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        (TBuilder)builder.RequireAuthorization(
            new AuthorizeAttribute { AuthenticationSchemes = $"{Cookie},{ApiKey}" });

    // Accepts EITHER the Cookie scheme OR the IdentityBearer scheme — the two HUMAN-facing
    // schemes. The browser panel authenticates with the Cookie while the desktop App presents its
    // signed-in user's IdentityBearer access token; both schemes set the SAME "userId" claim that
    // HttpContextCurrentUserAccessor resolves, and every handler on these routes loads data
    // user-scoped, so exposing a route to both is safe. The device ApiKey scheme is DELIBERATELY
    // EXCLUDED here: the pairs/accounts management surface is human-only, so a device key must never
    // unlock it (RequireApiKey / RequireCookieOrApiKey stay for the device-driven routes).
    public static TBuilder RequireCookieOrIdentityBearer<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        (TBuilder)builder.RequireAuthorization(
            new AuthorizeAttribute { AuthenticationSchemes = $"{Cookie},{IdentityBearer}" });

    // Accepts the Cookie, ApiKey OR IdentityBearer scheme. Used by /api/pairs/{id}/push, the
    // sync DATA path (it carries COM-read events to mirror), not part of the human-only MANAGEMENT
    // surface: the device background scheduler pushes under its ApiKey, while a human (panel cookie
    // or App IdentityBearer) may also push. All three schemes set the same "userId" claim and the
    // handler resolves the pair user-scoped, so admitting all three is safe.
    public static TBuilder RequireCookieOrApiKeyOrIdentityBearer<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        (TBuilder)builder.RequireAuthorization(
            new AuthorizeAttribute { AuthenticationSchemes = $"{Cookie},{ApiKey},{IdentityBearer}" });
}
