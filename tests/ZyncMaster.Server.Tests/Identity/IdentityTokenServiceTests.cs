using System;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZyncMaster.Server;
using ZyncMaster.Server.Data;
using ZyncMaster.Server.Tests.Storage;
using Xunit;

namespace ZyncMaster.Server.Tests.Identity;

// Mechanical behavior of the identity access/refresh token service (plan v2 §A-1): issue +
// validate, tamper/expiry/revocation rejection, refresh redeem + revoke-all. A mutable clock
// drives expiry deterministically; an ephemeral DataProtection provider mirrors the real one.
public class IdentityTokenServiceTests
{
    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now;
        public MutableClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static UserRow NewUser(string id = "u1") => new()
    {
        Id = id,
        Provider = "microsoft",
        Subject = "oid-" + id,
        Email = id + "@test",
        DisplayName = "User " + id,
        PrimaryEmail = id + "@primary",
        CreatedUtc = DateTimeOffset.UnixEpoch,
    };

    private static DataProtectionIdentityTokenService Build(
        EfStoreTestHarness h, TimeProvider clock, int accessTtlMinutes = 1440, int refreshTtlDays = 90)
    {
        var options = Options.Create(new ServerOptions
        {
            IdentityAccessTokenTtlMinutes = accessTtlMinutes,
            IdentityRefreshTokenTtlDays = refreshTtlDays,
        });
        return new DataProtectionIdentityTokenService(
            h.Factory, new EphemeralDataProtectionProvider(), options, clock);
    }

    [Fact]
    public async Task IssueAccessToken_then_validate_returns_principal_with_correct_claims()
    {
        using var h = new EfStoreTestHarness();
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var svc = Build(h, clock);
        var user = await SeedUserAsync(h, NewUser());

        var issued = svc.IssueAccessToken(user);
        issued.Token.Should().NotBeNullOrEmpty();
        issued.Jti.Should().NotBeNullOrEmpty();

        var principal = svc.ValidateAccessToken(issued.Token);

        principal.Should().NotBeNull();
        principal!.Jti.Should().Be(issued.Jti);
        principal.UserId.Should().Be(user.Id);
        principal.Email.Should().Be(user.PrimaryEmail);
        principal.DisplayName.Should().Be(user.DisplayName);
        principal.ExpiresAt.Should().Be(issued.ExpiresAt);
    }

    [Fact]
    public async Task ValidateAccessToken_returns_null_for_tampered_token()
    {
        using var h = new EfStoreTestHarness();
        var svc = Build(h, new MutableClock(DateTimeOffset.UnixEpoch));
        var issued = svc.IssueAccessToken(await SeedUserAsync(h, NewUser()));

        // Flip a character in the middle of the protected blob.
        var chars = issued.Token.ToCharArray();
        var mid = chars.Length / 2;
        chars[mid] = chars[mid] == 'A' ? 'B' : 'A';
        var tampered = new string(chars);

        svc.ValidateAccessToken(tampered).Should().BeNull();
    }

    [Fact]
    public void ValidateAccessToken_returns_null_for_garbage_input()
    {
        using var h = new EfStoreTestHarness();
        var svc = Build(h, new MutableClock(DateTimeOffset.UnixEpoch));

        svc.ValidateAccessToken("not-a-real-token").Should().BeNull();
        svc.ValidateAccessToken("").Should().BeNull();
    }

    [Fact]
    public async Task ValidateAccessToken_returns_null_after_expiry()
    {
        using var h = new EfStoreTestHarness();
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var svc = Build(h, clock, accessTtlMinutes: 60);
        var issued = svc.IssueAccessToken(await SeedUserAsync(h, NewUser()));

        svc.ValidateAccessToken(issued.Token).Should().NotBeNull();

        clock.Advance(TimeSpan.FromMinutes(61));

        svc.ValidateAccessToken(issued.Token).Should().BeNull();
    }

    [Fact]
    public async Task ValidateAccessToken_returns_null_after_jti_revoked()
    {
        using var h = new EfStoreTestHarness();
        var svc = Build(h, new MutableClock(DateTimeOffset.UnixEpoch));
        var issued = svc.IssueAccessToken(await SeedUserAsync(h, NewUser()));

        await svc.RevokeAccessAsync(issued.Jti);

        svc.ValidateAccessToken(issued.Token).Should().BeNull();
    }

    [Fact]
    public async Task RedeemRefreshToken_returns_owning_user()
    {
        using var h = new EfStoreTestHarness();
        var svc = Build(h, new MutableClock(DateTimeOffset.UnixEpoch));
        var user = await SeedUserAsync(h, NewUser());

        var refresh = await svc.IssueRefreshTokenAsync(user.Id);
        refresh.Should().NotBeNullOrEmpty();

        var redeemed = await svc.RedeemRefreshTokenAsync(refresh);

        redeemed.Should().NotBeNull();
        redeemed!.User.Id.Should().Be(user.Id);
        redeemed.NewRefreshToken.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RedeemRefreshToken_rotates_token_old_is_dead_new_works_once()
    {
        using var h = new EfStoreTestHarness();
        var svc = Build(h, new MutableClock(DateTimeOffset.UnixEpoch));
        var user = await SeedUserAsync(h, NewUser());

        var refresh = await svc.IssueRefreshTokenAsync(user.Id);

        // First redeem succeeds and hands back a brand-new refresh token.
        var first = await svc.RedeemRefreshTokenAsync(refresh);
        first.Should().NotBeNull();
        first!.NewRefreshToken.Should().NotBe(refresh);

        // The OLD token is now revoked — replaying it must fail (rotation defeats replay).
        (await svc.RedeemRefreshTokenAsync(refresh)).Should().BeNull();

        // The NEW token works exactly once, then it too is rotated away.
        var second = await svc.RedeemRefreshTokenAsync(first.NewRefreshToken);
        second.Should().NotBeNull();
        second!.User.Id.Should().Be(user.Id);

        (await svc.RedeemRefreshTokenAsync(first.NewRefreshToken)).Should().BeNull();
    }

    [Fact]
    public async Task RedeemRefreshToken_returns_null_for_unknown_or_tampered()
    {
        using var h = new EfStoreTestHarness();
        var svc = Build(h, new MutableClock(DateTimeOffset.UnixEpoch));
        var user = await SeedUserAsync(h, NewUser());
        var refresh = await svc.IssueRefreshTokenAsync(user.Id);

        (await svc.RedeemRefreshTokenAsync("never-issued")).Should().BeNull();
        (await svc.RedeemRefreshTokenAsync(refresh + "x")).Should().BeNull();
        (await svc.RedeemRefreshTokenAsync("")).Should().BeNull();
    }

    [Fact]
    public async Task RedeemRefreshToken_returns_null_after_expiry()
    {
        using var h = new EfStoreTestHarness();
        var clock = new MutableClock(DateTimeOffset.UnixEpoch);
        var svc = Build(h, clock, refreshTtlDays: 1);
        var user = await SeedUserAsync(h, NewUser());

        // Don't redeem before advancing — a successful redeem would rotate the token and the
        // later null would be "revoked", not "expired". Advance the clock past TTL, then assert
        // the still-live (never-redeemed) token is rejected purely on expiry.
        var refresh = await svc.IssueRefreshTokenAsync(user.Id);

        clock.Advance(TimeSpan.FromDays(2));

        (await svc.RedeemRefreshTokenAsync(refresh)).Should().BeNull();
    }

    [Fact]
    public async Task RevokeAllForUser_invalidates_refresh_and_access_tokens()
    {
        using var h = new EfStoreTestHarness();
        var svc = Build(h, new MutableClock(DateTimeOffset.UnixEpoch));
        var user = await SeedUserAsync(h, NewUser());

        var access = svc.IssueAccessToken(user);
        var refresh = await svc.IssueRefreshTokenAsync(user.Id);

        await svc.RevokeAllForUserAsync(user.Id);

        svc.ValidateAccessToken(access.Token).Should().BeNull();
        (await svc.RedeemRefreshTokenAsync(refresh)).Should().BeNull();
    }

    [Fact]
    public async Task ValidateAccessToken_returns_null_when_jti_row_deleted()
    {
        using var h = new EfStoreTestHarness();
        var svc = Build(h, new MutableClock(DateTimeOffset.UnixEpoch));
        var issued = svc.IssueAccessToken(await SeedUserAsync(h, NewUser()));

        // The blob itself is intact and unexpired, but its ledger row is gone. With no row to
        // confirm the jti is live, validation must fail closed (never trust the blob alone).
        await using (var db = h.NewContext())
        {
            var row = await db.IdentityAccessTokens.FirstAsync(r => r.Jti == issued.Jti);
            db.IdentityAccessTokens.Remove(row);
            await db.SaveChangesAsync();
        }

        svc.ValidateAccessToken(issued.Token).Should().BeNull();
    }

    [Fact]
    public async Task RevokeAllForUser_does_not_affect_other_users_tokens()
    {
        using var h = new EfStoreTestHarness();
        var svc = Build(h, new MutableClock(DateTimeOffset.UnixEpoch));
        var userA = await SeedUserAsync(h, NewUser("uA"));
        var userB = await SeedUserAsync(h, NewUser("uB"));

        var accessB = svc.IssueAccessToken(userB);
        var refreshB = await svc.IssueRefreshTokenAsync(userB.Id);

        // Revoking everything for A must leave B's session completely untouched.
        await svc.RevokeAllForUserAsync(userA.Id);

        svc.ValidateAccessToken(accessB.Token).Should().NotBeNull();
        (await svc.RedeemRefreshTokenAsync(refreshB)).Should().NotBeNull();
    }

    // Persists the user so the refresh-token FK and the redeem lookup resolve.
    private static async Task<UserRow> SeedUserAsync(EfStoreTestHarness h, UserRow user)
    {
        await using var db = h.NewContext();
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }
}
