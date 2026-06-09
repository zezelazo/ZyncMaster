using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using ZyncMaster.Server;
using ZyncMaster.Server.Data;

namespace ZyncMaster.Server.Postgres.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PurgePredicatesTests
{
    private readonly PostgresFixture _pg;
    public PurgePredicatesTests(PostgresFixture pg) => _pg = pg;

    [SkippableFact]
    public async Task ExpiredRows_AreDeleted_ByQuotedTimestamptzPredicate()
    {
        Skip.IfNot(_pg.Available, "No PostgreSQL (set ZYNCMASTER_TEST_PG).");

        var now = DateTimeOffset.UtcNow;
        var userId = DefaultCurrentUserAccessor.DefaultUserId;

        // Seed one expired + one live IdentityAccessToken. The same quoted, parameterised
        // timestamptz DELETE EphemeralPurgeService runs must drop ONLY the expired row — the
        // dialect risk SQLite cannot validate (quoted idents + timestamptz comparison).
        var expiredJti = Guid.NewGuid().ToString("N");
        var liveJti = Guid.NewGuid().ToString("N");

        using (var seed = _pg.NewContext())
        {
            seed.IdentityAccessTokens.Add(new IdentityAccessTokenRow
            {
                Jti = expiredJti,
                UserId = userId,
                IssuedAt = now.AddHours(-2),
                ExpiresAt = now.AddHours(-1), // already expired
                RevokedAt = null,
            });
            seed.IdentityAccessTokens.Add(new IdentityAccessTokenRow
            {
                Jti = liveJti,
                UserId = userId,
                IssuedAt = now,
                ExpiresAt = now.AddHours(1), // still live
                RevokedAt = null,
            });
            await seed.SaveChangesAsync();
        }

        using var db = _pg.NewContext();
        var cutoff = now;
        var deleted = await db.Database.ExecuteSqlInterpolatedAsync(
            $@"DELETE FROM ""IdentityAccessTokens"" WHERE ""ExpiresAt"" <= {cutoff}");

        deleted.Should().Be(1);

        using var verify = _pg.NewContext();
        (await verify.IdentityAccessTokens.AnyAsync(t => t.Jti == expiredJti)).Should().BeFalse();
        (await verify.IdentityAccessTokens.AnyAsync(t => t.Jti == liveJti)).Should().BeTrue();
    }
}
