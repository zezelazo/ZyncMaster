using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ZyncMaster.Server;

// Authenticates requests carrying "Authorization: Bearer <token>" against the internal identity
// access token (plan v2 §A-2, body Phase 3). The token is validated by IIdentityTokenService;
// on success the handler stamps a principal carrying the same userId claim the Cookie/ApiKey
// schemes set (HttpContextCurrentUserAccessor.UserIdClaimType) so ICurrentUserAccessor resolves
// the caller identically regardless of how they authenticated. This scheme backs /api/identity/me
// today and the upcoming calendar-account (Track A-2) and device-registration (Track B) endpoints.
//
// A missing or non-Bearer Authorization header yields NoResult() (the request is simply not
// authenticated by this scheme, producing a 401 when the scheme is required), while a present but
// invalid/expired/revoked token yields Fail().
public sealed class IdentityBearerAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "IdentityBearer";
    public const string HeaderName = "Authorization";

    private const string BearerPrefix = "Bearer ";

    private readonly IIdentityTokenService _tokenService;

    public IdentityBearerAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IIdentityTokenService tokenService)
        : base(options, logger, encoder)
    {
        ArgumentNullException.ThrowIfNull(tokenService);
        _tokenService = tokenService;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values))
            return Task.FromResult(AuthenticateResult.NoResult());

        var raw = values.ToString();
        if (string.IsNullOrWhiteSpace(raw) ||
            !raw.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var token = raw[BearerPrefix.Length..].Trim();
        if (string.IsNullOrEmpty(token))
            return Task.FromResult(AuthenticateResult.NoResult());

        var principalInfo = _tokenService.ValidateAccessToken(token);
        if (principalInfo is null)
            return Task.FromResult(AuthenticateResult.Fail("Invalid identity bearer token."));

        var principal = AuthSchemes.BuildIdentityBearerPrincipal(
            principalInfo.UserId, principalInfo.Email, principalInfo.DisplayName);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
