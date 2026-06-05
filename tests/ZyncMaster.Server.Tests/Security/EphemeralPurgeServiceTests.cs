using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ZyncMaster.Server;
using ZyncMaster.Server.Data;
using ZyncMaster.Server.Tests.Storage;
using Xunit;

namespace ZyncMaster.Server.Tests.Security;

// §A/§D — the ephemeral-table hygiene job. These tests pin the PURGE LOGIC (what is deleted and
// what is preserved) directly through PurgeOnceAsync(now), with no timer, against the SQLite
// harness. The background timer itself is not exercised here (and is gated off under the test
// host in Program.cs), so the suite never hangs on a long-running BackgroundService.
public sealed class EphemeralPurgeServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static EphemeralPurgeService NewService(EfStoreTestHarness harness) =>
        new(harness.Factory, NullLogger<EphemeralPurgeService>.Instance);

    private static IdentityAccessTokenRow Access(string jti, DateTimeOffset expires, DateTimeOffset? revoked = null) =>
        new() { Jti = jti, UserId = DefaultCurrentUserAccessor.DefaultUserId, IssuedAt = Now.AddHours(-1), ExpiresAt = expires, RevokedAt = revoked };

    private static IdentityRefreshTokenRow Refresh(string id, DateTimeOffset expires, DateTimeOffset? revoked = null) =>
        new() { Id = id, UserId = DefaultCurrentUserAccessor.DefaultUserId, TokenHash = "h-" + id, IssuedAt = Now.AddHours(-1), ExpiresAt = expires, RevokedAt = revoked };

    private static MagicLinkRow Magic(string id, DateTimeOffset expires, DateTimeOffset? consumed = null) =>
        new() { Id = id, TokenHash = "h-" + id, Email = id + "@test", Port = 1, Nonce = "n", CreatedAt = Now.AddMinutes(-5), ExpiresAt = expires, ConsumedAt = consumed };

    private static SyncRunLockRow Lock(string pairId, DateTimeOffset until) =>
        new() { PairId = pairId, LockedUntil = until, Owner = "o" };

    [Fact]
    public async Task PurgeOnceAsync_deletes_expired_access_tokens_only()
    {
        using var harness = new EfStoreTestHarness();
        await using (var db = harness.NewContext())
        {
            db.IdentityAccessTokens.Add(Access("expired", Now.AddMinutes(-1)));
            db.IdentityAccessTokens.Add(Access("live", Now.AddHours(1)));
            // CRITICAL: revoked-but-not-expired MUST survive so revocation keeps being enforced.
            db.IdentityAccessTokens.Add(Access("revoked-live", Now.AddHours(1), revoked: Now.AddMinutes(-1)));
            await db.SaveChangesAsync();
        }

        await NewService(harness).PurgeOnceAsync(Now);

        await using var verify = harness.NewContext();
        var ids = verify.IdentityAccessTokens.Select(t => t.Jti).ToList();
        ids.Should().BeEquivalentTo(new[] { "live", "revoked-live" });
    }

    [Fact]
    public async Task PurgeOnceAsync_preserves_revoked_but_unexpired_refresh_token()
    {
        using var harness = new EfStoreTestHarness();
        await using (var db = harness.NewContext())
        {
            db.IdentityRefreshTokens.Add(Refresh("expired", Now.AddMinutes(-1)));
            db.IdentityRefreshTokens.Add(Refresh("live", Now.AddDays(7)));
            db.IdentityRefreshTokens.Add(Refresh("revoked-live", Now.AddDays(7), revoked: Now.AddMinutes(-1)));
            await db.SaveChangesAsync();
        }

        await NewService(harness).PurgeOnceAsync(Now);

        await using var verify = harness.NewContext();
        var ids = verify.IdentityRefreshTokens.Select(t => t.Id).ToList();
        ids.Should().BeEquivalentTo(new[] { "live", "revoked-live" });
    }

    [Fact]
    public async Task PurgeOnceAsync_deletes_expired_or_consumed_magic_links()
    {
        using var harness = new EfStoreTestHarness();
        await using (var db = harness.NewContext())
        {
            db.MagicLinks.Add(Magic("expired", Now.AddMinutes(-1)));
            db.MagicLinks.Add(Magic("consumed", Now.AddMinutes(10), consumed: Now.AddMinutes(-2)));
            db.MagicLinks.Add(Magic("live", Now.AddMinutes(10)));
            await db.SaveChangesAsync();
        }

        await NewService(harness).PurgeOnceAsync(Now);

        await using var verify = harness.NewContext();
        var ids = verify.MagicLinks.Select(m => m.Id).ToList();
        ids.Should().BeEquivalentTo(new[] { "live" });
    }

    [Fact]
    public async Task PurgeOnceAsync_deletes_expired_run_locks_only()
    {
        using var harness = new EfStoreTestHarness();
        await using (var db = harness.NewContext())
        {
            db.SyncRunLocks.Add(Lock("expired", Now.AddMinutes(-1)));
            db.SyncRunLocks.Add(Lock("held", Now.AddMinutes(5)));
            await db.SaveChangesAsync();
        }

        await NewService(harness).PurgeOnceAsync(Now);

        await using var verify = harness.NewContext();
        var ids = verify.SyncRunLocks.Select(l => l.PairId).ToList();
        ids.Should().BeEquivalentTo(new[] { "held" });
    }

    [Fact]
    public async Task PurgeOnceAsync_on_empty_tables_is_a_noop()
    {
        using var harness = new EfStoreTestHarness();

        var act = async () => await NewService(harness).PurgeOnceAsync(Now);

        await act.Should().NotThrowAsync();
    }

    // FIX A — pending pairings are now swept by the same purge sweep (previously they were NEVER
    // purged or expired). With the default 15-minute TTL the cutoff is Now-15m = 11:45; rows older
    // than that are deleted regardless of Approved, fresher rows survive.
    [Fact]
    public async Task PurgeOnceAsync_deletes_expired_pending_pairings_only()
    {
        using var harness = new EfStoreTestHarness();
        await using (var db = harness.NewContext())
        {
            db.PendingPairings.Add(Pending("expired", Now.AddMinutes(-30)));
            db.PendingPairings.Add(Pending("expired-approved", Now.AddMinutes(-20), approved: true));
            db.PendingPairings.Add(Pending("fresh", Now.AddMinutes(-1)));
            await db.SaveChangesAsync();
        }

        await NewService(harness).PurgeOnceAsync(Now);

        await using var verify = harness.NewContext();
        var ids = verify.PendingPairings.Select(p => p.PairingId).ToList();
        ids.Should().BeEquivalentTo(new[] { "fresh" });
    }

    private static PendingPairingRow Pending(string id, DateTimeOffset created, bool approved = false) =>
        new()
        {
            PairingId = id,
            DeviceName = "Device " + id,
            Code = "C-" + id,
            Approved = approved,
            CreatedUtc = created,
        };
}
