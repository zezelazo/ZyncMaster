using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZyncMaster.Server.Data;

namespace ZyncMaster.Server;

public sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    private readonly IDbContextFactory<ZyncMasterDbContext> _factory;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IDbContextFactory<ZyncMasterDbContext> factory)
        : base(options, logger, encoder)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values))
            return AuthenticateResult.NoResult();

        var key = values.ToString();
        if (string.IsNullOrEmpty(key))
            return AuthenticateResult.NoResult();

        // Authentication runs before the current user is known, so the device must be
        // matched ACROSS all users (the user-scoped IDeviceStore would only see the
        // ambient "default" user). The matched device's UserId then seeds the principal.
        await using var db = await _factory.CreateDbContextAsync(Context.RequestAborted);
        var rows = await db.Devices.ToListAsync(Context.RequestAborted);
        var device = rows.FirstOrDefault(d => ApiKeyHasher.Verify(key, d.ApiKeyHash));
        if (device is null)
            return AuthenticateResult.Fail("Invalid API key");

        device.LastSeenUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(Context.RequestAborted);

        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim("deviceId", device.Id),
                new Claim(HttpContextCurrentUserAccessor.UserIdClaimType, device.UserId),
            },
            SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return AuthenticateResult.Success(ticket);
    }
}
