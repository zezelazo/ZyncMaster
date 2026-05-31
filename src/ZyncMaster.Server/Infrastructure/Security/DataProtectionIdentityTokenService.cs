using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZyncMaster.Server.Data;

namespace ZyncMaster.Server;

// DataProtection-backed implementation of IIdentityTokenService (plan v2 §A-1).
//
// Access token  = a JSON payload (jti/userId/email/displayName/iat/exp) serialized and
//                 protected with a dedicated IDataProtector purpose. Tampering invalidates
//                 the unprotect; expiry and revocation are checked against the payload and
//                 the IdentityAccessTokens ledger respectively.
// Refresh token = 32 random bytes, base64url. Only its SHA-256 hash is persisted, so the
//                 store never holds a usable refresh token.
//
// A TimeProvider seam makes TTL/expiry deterministic in tests; it defaults to the system
// clock in production.
public sealed class DataProtectionIdentityTokenService : IIdentityTokenService
{
    private const string ProtectorPurpose = "ZyncMaster.IdentityToken";

    private readonly IDbContextFactory<ZyncMasterDbContext> _factory;
    private readonly IDataProtector _protector;
    private readonly ServerOptions _options;
    private readonly TimeProvider _clock;

    public DataProtectionIdentityTokenService(
        IDbContextFactory<ZyncMasterDbContext> factory,
        IDataProtectionProvider dp,
        IOptions<ServerOptions> options,
        TimeProvider? clock = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        ArgumentNullException.ThrowIfNull(dp);
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _protector = dp.CreateProtector(ProtectorPurpose);
        _clock = clock ?? TimeProvider.System;
    }

    public IdentityToken IssueAccessToken(UserRow user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var now = _clock.GetUtcNow();
        var expires = now.AddMinutes(_options.IdentityAccessTokenTtlMinutes);
        var jti = Guid.NewGuid().ToString("N");

        var payload = new AccessTokenPayload
        {
            Jti = jti,
            UserId = user.Id,
            Email = user.PrimaryEmail ?? user.Email ?? "",
            DisplayName = user.DisplayName ?? "",
            IssuedAtUnix = now.ToUnixTimeSeconds(),
            ExpiresAtUnix = expires.ToUnixTimeSeconds(),
        };

        var token = _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(payload));
        var tokenString = ToBase64Url(token);

        // Register the jti synchronously so it can be revoked. The factory creates a fresh
        // context per call, mirroring the other EF stores.
        using var db = _factory.CreateDbContext();
        db.IdentityAccessTokens.Add(new IdentityAccessTokenRow
        {
            Jti = jti,
            UserId = user.Id,
            IssuedAt = now,
            ExpiresAt = expires,
            RevokedAt = null,
        });
        db.SaveChanges();

        return new IdentityToken(tokenString, jti, expires);
    }

    public IdentityPrincipal? ValidateAccessToken(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        AccessTokenPayload? payload;
        try
        {
            var protectedBytes = FromBase64Url(token);
            var json = _protector.Unprotect(protectedBytes);
            payload = JsonSerializer.Deserialize<AccessTokenPayload>(json);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
        {
            // Tampered, truncated, or not one of our tokens.
            return null;
        }

        if (payload is null || string.IsNullOrEmpty(payload.Jti) || string.IsNullOrEmpty(payload.UserId))
        {
            return null;
        }

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAtUnix);
        if (expiresAt <= _clock.GetUtcNow())
        {
            return null;
        }

        // Revocation check: the jti must still be present and not revoked.
        using var db = _factory.CreateDbContext();
        var row = db.IdentityAccessTokens.AsNoTracking().FirstOrDefault(r => r.Jti == payload.Jti);
        if (row is null || row.RevokedAt is not null)
        {
            return null;
        }

        return new IdentityPrincipal(
            payload.Jti,
            payload.UserId,
            payload.Email,
            payload.DisplayName,
            DateTimeOffset.FromUnixTimeSeconds(payload.IssuedAtUnix),
            expiresAt);
    }

    public async Task<string> IssueRefreshTokenAsync(string userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userId);

        var now = _clock.GetUtcNow();
        var raw = RandomNumberGenerator.GetBytes(32);
        var tokenValue = ToBase64Url(raw);

        await using var db = await _factory.CreateDbContextAsync(ct);
        db.IdentityRefreshTokens.Add(new IdentityRefreshTokenRow
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId,
            TokenHash = HashToken(tokenValue),
            IssuedAt = now,
            ExpiresAt = now.AddDays(_options.IdentityRefreshTokenTtlDays),
            RevokedAt = null,
        });
        await db.SaveChangesAsync(ct);

        return tokenValue;
    }

    public async Task<UserRow?> RedeemRefreshTokenAsync(string refreshToken, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(refreshToken))
        {
            return null;
        }

        var hash = HashToken(refreshToken);

        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.IdentityRefreshTokens.AsNoTracking()
            .FirstOrDefaultAsync(r => r.TokenHash == hash, ct);

        if (row is null || row.RevokedAt is not null || row.ExpiresAt <= _clock.GetUtcNow())
        {
            return null;
        }

        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == row.UserId, ct);
    }

    public async Task RevokeAccessAsync(string jti, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(jti);
        var now = _clock.GetUtcNow();
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.IdentityAccessTokens
            .Where(r => r.Jti == jti && r.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, now), ct);
    }

    public async Task RevokeAllForUserAsync(string userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userId);
        var now = _clock.GetUtcNow();
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.IdentityAccessTokens
            .Where(r => r.UserId == userId && r.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, now), ct);
        await db.IdentityRefreshTokens
            .Where(r => r.UserId == userId && r.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAt, now), ct);
    }

    private static string HashToken(string tokenValue)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(tokenValue));
        return ToBase64Url(bytes);
    }

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    // Internal payload shape. Compact JSON property names keep the protected blob small.
    private sealed class AccessTokenPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("jti")]
        public string Jti { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("uid")]
        public string UserId { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("eml")]
        public string Email { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("dn")]
        public string DisplayName { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("iat")]
        public long IssuedAtUnix { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("exp")]
        public long ExpiresAtUnix { get; set; }
    }
}
