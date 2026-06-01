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

        // §A-3 — indexed O(1) lookup. A modern key is "keyId.secret": the public keyId locates
        // the single candidate device via the KeyId index, then we run PBKDF2 ONCE against that
        // device's stored secret hash. This replaces the former O(n) full scan that ran PBKDF2
        // for every device on every request (a trivial DoS amplifier).
        DeviceRow? device;
        if (ApiKeyGenerator.TryParse(key, out var keyId, out var secret))
        {
            device = await db.Devices
                .FirstOrDefaultAsync(d => d.KeyId == keyId, Context.RequestAborted);
            // Verify only the secret half against the located row's hash. A keyId that matches a
            // row but whose secret fails PBKDF2 is rejected (no fall-through to a scan).
            if (device is null || !ApiKeyHasher.Verify(secret, device.ApiKeyHash))
                return AuthenticateResult.Fail("Invalid API key");
        }
        else
        {
            // Legacy key (no keyId separator, minted before §A-3). These have no indexed handle,
            // so fall back to the scan + per-row PBKDF2. Re-issuing the key (re-pair) upgrades the
            // device to the indexed path; this branch only keeps already-deployed keys working.
            var legacy = await db.Devices
                .Where(d => d.KeyId == null)
                .ToListAsync(Context.RequestAborted);
            device = legacy.FirstOrDefault(d => ApiKeyHasher.Verify(key, d.ApiKeyHash));
            if (device is null)
                return AuthenticateResult.Fail("Invalid API key");
        }

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
