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

    // FIX F — LastSeenUtc is a coarse "last activity" timestamp, not an audit log. Persisting it on
    // EVERY request is pure write amplification (one UPDATE per request, far worse now that the App
    // heartbeats), so we only write it when it has drifted by more than this window. A few minutes of
    // staleness on a liveness timestamp is irrelevant; the lease (LeaseUntil, renewed on register /
    // heartbeat / push) is the authoritative "App running" signal, not LastSeenUtc.
    private static readonly TimeSpan LastSeenThrottle = TimeSpan.FromMinutes(5);

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
        string deviceId;
        string deviceUserId;
        DateTimeOffset? lastSeen;
        if (ApiKeyGenerator.TryParse(key, out var keyId, out var secret))
        {
            // Project only the columns auth needs (no tracking, no full entity) so a hot auth path
            // does not materialize/track a whole DeviceRow on every request.
            var candidate = await db.Devices
                .AsNoTracking()
                .Where(d => d.KeyId == keyId)
                .Select(d => new { d.Id, d.UserId, d.ApiKeyHash, d.LastSeenUtc })
                .FirstOrDefaultAsync(Context.RequestAborted);
            // Verify only the secret half against the located row's hash. A keyId that matches a
            // row but whose secret fails PBKDF2 is rejected (no fall-through to a scan).
            if (candidate is null || !ApiKeyHasher.Verify(secret, candidate.ApiKeyHash))
                return AuthenticateResult.Fail("Invalid API key");
            (deviceId, deviceUserId, lastSeen) = (candidate.Id, candidate.UserId, candidate.LastSeenUtc);
        }
        else
        {
            // Legacy key (no keyId separator, minted before §A-3). These have no indexed handle, so
            // fall back to a scan + per-row PBKDF2. We project ONLY (Id, UserId, hash, LastSeenUtc)
            // for legacy-only rows so the scan never materializes full entities, and re-issuing the
            // key (re-pair) upgrades the device to the O(1) indexed path above. This branch only
            // keeps already-deployed legacy keys working and shrinks to nothing as devices re-pair.
            var legacy = await db.Devices
                .AsNoTracking()
                .Where(d => d.KeyId == null)
                .Select(d => new { d.Id, d.UserId, d.ApiKeyHash, d.LastSeenUtc })
                .ToListAsync(Context.RequestAborted);
            var match = legacy.FirstOrDefault(d => ApiKeyHasher.Verify(key, d.ApiKeyHash));
            if (match is null)
                return AuthenticateResult.Fail("Invalid API key");
            (deviceId, deviceUserId, lastSeen) = (match.Id, match.UserId, match.LastSeenUtc);
        }

        // FIX F — throttled LastSeenUtc. Only issue the UPDATE when the stored value has drifted
        // past the throttle window, and do it as a targeted ExecuteUpdate (no entity tracking /
        // SaveChanges round-trip). The vast majority of requests therefore perform ZERO writes.
        var now = DateTimeOffset.UtcNow;
        if (lastSeen is null || now - lastSeen.Value > LastSeenThrottle)
        {
            await db.Devices
                .Where(d => d.Id == deviceId)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastSeenUtc, now), Context.RequestAborted);
        }

        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim("deviceId", deviceId),
                new Claim(HttpContextCurrentUserAccessor.UserIdClaimType, deviceUserId),
            },
            SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return AuthenticateResult.Success(ticket);
    }
}
