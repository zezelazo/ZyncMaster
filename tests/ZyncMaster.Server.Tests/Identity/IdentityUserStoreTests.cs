using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ZyncMaster.Server.Tests.Storage;
using Xunit;

namespace ZyncMaster.Server.Tests.Identity;

// Store-level (mechanical) behavior of multi-provider identity logins. Security policy
// (which providers may be trusted for emailVerified, proof-of-possession, etc.) lives at
// the endpoint layer and is NOT exercised here.
public class IdentityUserStoreTests
{
    [Fact]
    public async Task UpsertByLogin_creates_new_user_and_login()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfUserStore(h.Factory);

        var user = await store.UpsertByLoginAsync("microsoft", "oid-1", "a@test", true, "Alice");

        user.Id.Should().NotBeNullOrEmpty();
        user.PrimaryEmail.Should().Be("a@test");
        user.DisplayName.Should().Be("Alice");

        await using var db = h.NewContext();
        var login = await db.IdentityLogins.SingleAsync(l => l.Provider == "microsoft" && l.ProviderSubject == "oid-1");
        login.UserId.Should().Be(user.Id);
        login.Email.Should().Be("a@test");
        login.EmailVerified.Should().BeTrue();
    }

    [Fact]
    public async Task UpsertByLogin_on_existing_login_updates_and_does_not_duplicate()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfUserStore(h.Factory);

        var first = await store.UpsertByLoginAsync("microsoft", "oid-1", "old@test", true, "Old");
        var second = await store.UpsertByLoginAsync("microsoft", "oid-1", "new@test", true, "New");

        second.Id.Should().Be(first.Id);

        await using var db = h.NewContext();
        var logins = await db.IdentityLogins.Where(l => l.Provider == "microsoft" && l.ProviderSubject == "oid-1").ToListAsync();
        logins.Should().HaveCount(1);
        logins[0].Email.Should().Be("new@test");

        var user = await db.Users.SingleAsync(u => u.Id == first.Id);
        user.DisplayName.Should().Be("New");
        user.PrimaryEmail.Should().Be("new@test");

        // No extra users created (excluding the seeded default).
        (await db.Users.CountAsync(u => u.Id != DefaultCurrentUserAccessor.DefaultUserId)).Should().Be(1);
    }

    [Fact]
    public async Task UpsertByLogin_with_verified_email_matching_another_verified_login_links_to_same_user()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfUserStore(h.Factory);

        var msUser = await store.UpsertByLoginAsync("microsoft", "oid-1", "shared@test", true, "Shared");
        var googleUser = await store.UpsertByLoginAsync("google", "g-1", "shared@test", true, "Shared G");

        // Linked => same canonical user, two logins.
        googleUser.Id.Should().Be(msUser.Id);

        await using var db = h.NewContext();
        var logins = await db.IdentityLogins.Where(l => l.UserId == msUser.Id).ToListAsync();
        logins.Should().HaveCount(2);
        logins.Select(l => l.Provider).Should().BeEquivalentTo(new[] { "microsoft", "google" });

        // Exactly one non-default user.
        (await db.Users.CountAsync(u => u.Id != DefaultCurrentUserAccessor.DefaultUserId)).Should().Be(1);
    }

    [Fact]
    public async Task UpsertByLogin_with_unverified_email_does_not_auto_link()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfUserStore(h.Factory);

        var msUser = await store.UpsertByLoginAsync("microsoft", "oid-1", "shared@test", true, "Shared");
        var googleUser = await store.UpsertByLoginAsync("google", "g-1", "shared@test", false, "Shared G");

        // Unverified => brand new user, no link.
        googleUser.Id.Should().NotBe(msUser.Id);

        await using var db = h.NewContext();
        (await db.Users.CountAsync(u => u.Id != DefaultCurrentUserAccessor.DefaultUserId)).Should().Be(2);
    }

    [Fact]
    public async Task UpsertByLogin_does_not_link_to_unverified_existing_login_even_when_incoming_is_verified()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfUserStore(h.Factory);

        var msUser = await store.UpsertByLoginAsync("microsoft", "oid-1", "shared@test", false, "Shared");
        var googleUser = await store.UpsertByLoginAsync("google", "g-1", "shared@test", true, "Shared G");

        // The existing login's email is not verified, so there is nothing to link to.
        googleUser.Id.Should().NotBe(msUser.Id);
    }

    [Fact]
    public async Task TryLinkByEmail_with_verified_match_links_and_returns_user()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfUserStore(h.Factory);

        var msUser = await store.UpsertByLoginAsync("microsoft", "oid-1", "shared@test", true, "Shared");

        var linked = await store.TryLinkByEmailAsync("google", "g-1", "shared@test", true, "Shared G");

        linked.Should().NotBeNull();
        linked!.Id.Should().Be(msUser.Id);

        await using var db = h.NewContext();
        (await db.IdentityLogins.CountAsync(l => l.UserId == msUser.Id)).Should().Be(2);
    }

    [Fact]
    public async Task TryLinkByEmail_returns_null_when_unverified()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfUserStore(h.Factory);

        await store.UpsertByLoginAsync("microsoft", "oid-1", "shared@test", true, "Shared");

        var linked = await store.TryLinkByEmailAsync("google", "g-1", "shared@test", false, "Shared G");

        linked.Should().BeNull();

        await using var db = h.NewContext();
        (await db.IdentityLogins.CountAsync(l => l.Provider == "google")).Should().Be(0);
    }

    [Fact]
    public async Task TryLinkByEmail_returns_null_when_no_match()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfUserStore(h.Factory);

        await store.UpsertByLoginAsync("microsoft", "oid-1", "shared@test", true, "Shared");

        var linked = await store.TryLinkByEmailAsync("google", "g-1", "nobody@test", true, "Nobody");

        linked.Should().BeNull();
    }

    [Fact]
    public async Task UpsertByLogin_throws_when_verified_email_shared_by_multiple_users()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfUserStore(h.Factory);

        // Two verified logins with the same email on DISTINCT users, inserted by hand so the
        // store's own linking can't collapse them first.
        await using (var seed = h.NewContext())
        {
            var u1 = NewUser("conflict@test");
            var u2 = NewUser("conflict@test");
            seed.Users.AddRange(u1, u2);
            seed.IdentityLogins.Add(NewLoginRow(u1.Id, "microsoft", "oid-1", "conflict@test", true));
            seed.IdentityLogins.Add(NewLoginRow(u2.Id, "google", "g-1", "conflict@test", true));
            await seed.SaveChangesAsync();
        }

        var act = () => store.UpsertByLoginAsync("facebook", "fb-1", "conflict@test", true, "Conflict");

        await act.Should().ThrowAsync<System.InvalidOperationException>()
            .WithMessage("*multiple users share verified email*");
    }

    [Fact]
    public async Task TryLinkByEmail_throws_when_verified_email_shared_by_multiple_users()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfUserStore(h.Factory);

        await using (var seed = h.NewContext())
        {
            var u1 = NewUser("conflict@test");
            var u2 = NewUser("conflict@test");
            seed.Users.AddRange(u1, u2);
            seed.IdentityLogins.Add(NewLoginRow(u1.Id, "microsoft", "oid-1", "conflict@test", true));
            seed.IdentityLogins.Add(NewLoginRow(u2.Id, "google", "g-1", "conflict@test", true));
            await seed.SaveChangesAsync();
        }

        var act = () => store.TryLinkByEmailAsync("facebook", "fb-1", "conflict@test", true, "Conflict");

        await act.Should().ThrowAsync<System.InvalidOperationException>()
            .WithMessage("*multiple users share verified email*");
    }

    [Fact]
    public async Task UpsertByLogin_normalizes_provider_and_email_and_does_not_duplicate()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfUserStore(h.Factory);

        // Mixed case + surrounding whitespace, then a normalized repeat for the same subject.
        var first = await store.UpsertByLoginAsync("Microsoft", "oid-1", " Alice@Test.com ", true, "Alice");
        var second = await store.UpsertByLoginAsync("microsoft", "oid-1", "alice@test.com", true, "Alice2");

        second.Id.Should().Be(first.Id);

        await using var db = h.NewContext();
        var logins = await db.IdentityLogins
            .Where(l => l.ProviderSubject == "oid-1").ToListAsync();
        logins.Should().HaveCount(1);
        logins[0].Provider.Should().Be("microsoft");
        logins[0].Email.Should().Be("alice@test.com");
        second.PrimaryEmail.Should().Be("alice@test.com");
    }

    private static ZyncMaster.Server.Data.UserRow NewUser(string email) => new()
    {
        Id = System.Guid.NewGuid().ToString("N"),
        Provider = "test",
        Subject = System.Guid.NewGuid().ToString("N"),
        Email = email,
        DisplayName = "Seed",
        CreatedUtc = System.DateTimeOffset.UtcNow,
        PrimaryEmail = email,
        Plan = null,
    };

    private static ZyncMaster.Server.Data.IdentityLoginRow NewLoginRow(
        string userId, string provider, string providerSubject, string email, bool emailVerified) => new()
    {
        Id = System.Guid.NewGuid().ToString("N"),
        UserId = userId,
        Provider = provider,
        ProviderSubject = providerSubject,
        Email = email,
        EmailVerified = emailVerified,
        LinkedAt = System.DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Legacy_UpsertAsync_still_works()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfUserStore(h.Factory);

        var first = await store.UpsertAsync("microsoft", "oid-1", "a@test", "Alice");
        var second = await store.UpsertAsync("microsoft", "oid-1", "b@test", "Bob");

        second.Id.Should().Be(first.Id);
        second.Email.Should().Be("b@test");
        second.DisplayName.Should().Be("Bob");
    }

    // ----- Orphan-repoint migration (the already-created split user) -----------------------

    [Fact]
    public async Task UpsertByLogin_repoints_orphan_microsoft_login_to_verified_email_user_and_removes_empty_orphan()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfUserStore(h.Factory);

        // U1: the magic-link user that owns the data, with a verified local login.
        var u1 = await store.UpsertByLoginAsync("local", "owner@test", "owner@test", true, "Owner");
        // A device U1 owns, so U1 is clearly the canonical, data-owning user.
        await using (var seed = h.NewContext())
        {
            seed.Devices.Add(NewDevice(u1.Id));
            await seed.SaveChangesAsync();
        }

        // U2: the empty orphan minted by the OLD Microsoft flow (emailVerified:false back then).
        await using (var seed = h.NewContext())
        {
            var u2 = NewUser("owner@test");
            seed.Users.Add(u2);
            seed.IdentityLogins.Add(NewLoginRow(u2.Id, "microsoft", "ms-oid", "owner@test", false));
            await seed.SaveChangesAsync();
        }

        // The Microsoft sign-in now arrives verified: the existing login must repoint to U1.
        var resolved = await store.UpsertByLoginAsync("microsoft", "ms-oid", "owner@test", true, "Owner MS");

        resolved.Id.Should().Be(u1.Id);

        await using var db = h.NewContext();
        // The Microsoft login now points at U1.
        var msLogin = await db.IdentityLogins.SingleAsync(l => l.Provider == "microsoft" && l.ProviderSubject == "ms-oid");
        msLogin.UserId.Should().Be(u1.Id);
        msLogin.EmailVerified.Should().BeTrue();
        // U1 now owns both logins.
        (await db.IdentityLogins.CountAsync(l => l.UserId == u1.Id)).Should().Be(2);
        // The empty orphan user is gone.
        (await db.Users.AnyAsync(u => u.PrimaryEmail == "owner@test" && u.Id != u1.Id)).Should().BeFalse();
        // U1 keeps its device.
        (await db.Devices.CountAsync(d => d.UserId == u1.Id)).Should().Be(1);
    }

    [Fact]
    public async Task UpsertByLogin_repoint_is_idempotent_on_a_second_signin()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfUserStore(h.Factory);

        var u1 = await store.UpsertByLoginAsync("local", "owner@test", "owner@test", true, "Owner");
        await using (var seed = h.NewContext())
        {
            var u2 = NewUser("owner@test");
            seed.Users.Add(u2);
            seed.IdentityLogins.Add(NewLoginRow(u2.Id, "microsoft", "ms-oid", "owner@test", false));
            await seed.SaveChangesAsync();
        }

        var first = await store.UpsertByLoginAsync("microsoft", "ms-oid", "owner@test", true, "Owner MS");
        var second = await store.UpsertByLoginAsync("microsoft", "ms-oid", "owner@test", true, "Owner MS Again");

        first.Id.Should().Be(u1.Id);
        second.Id.Should().Be(u1.Id);

        await using var db = h.NewContext();
        (await db.IdentityLogins.CountAsync(l => l.UserId == u1.Id)).Should().Be(2);
        (await db.Users.CountAsync(u => u.Id != DefaultCurrentUserAccessor.DefaultUserId)).Should().Be(1);
    }

    [Fact]
    public async Task UpsertByLogin_does_not_remove_orphan_that_still_owns_data()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfUserStore(h.Factory);

        var u1 = await store.UpsertByLoginAsync("local", "owner@test", "owner@test", true, "Owner");

        // The orphan still owns a device — repoint the login but KEEP the user.
        string orphanId;
        await using (var seed = h.NewContext())
        {
            var u2 = NewUser("owner@test");
            orphanId = u2.Id;
            seed.Users.Add(u2);
            seed.IdentityLogins.Add(NewLoginRow(u2.Id, "microsoft", "ms-oid", "owner@test", false));
            seed.Devices.Add(NewDevice(u2.Id));
            await seed.SaveChangesAsync();
        }

        var resolved = await store.UpsertByLoginAsync("microsoft", "ms-oid", "owner@test", true, "Owner MS");

        resolved.Id.Should().Be(u1.Id);

        await using var db = h.NewContext();
        // The login moved to U1.
        (await db.IdentityLogins.SingleAsync(l => l.ProviderSubject == "ms-oid")).UserId.Should().Be(u1.Id);
        // But the data-owning user is preserved (its device blocks deletion).
        (await db.Users.AnyAsync(u => u.Id == orphanId)).Should().BeTrue();
        (await db.Devices.CountAsync(d => d.UserId == orphanId)).Should().Be(1);
    }

    [Fact]
    public async Task UpsertByLogin_does_NOT_repoint_when_incoming_microsoft_email_is_unverified()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfUserStore(h.Factory);

        var u1 = await store.UpsertByLoginAsync("local", "owner@test", "owner@test", true, "Owner");
        string orphanId;
        await using (var seed = h.NewContext())
        {
            var u2 = NewUser("owner@test");
            orphanId = u2.Id;
            seed.Users.Add(u2);
            seed.IdentityLogins.Add(NewLoginRow(u2.Id, "microsoft", "ms-oid", "owner@test", false));
            await seed.SaveChangesAsync();
        }

        // SECURITY: an UNVERIFIED incoming email must never repoint/merge.
        var resolved = await store.UpsertByLoginAsync("microsoft", "ms-oid", "owner@test", false, "Owner MS");

        resolved.Id.Should().Be(orphanId);
        resolved.Id.Should().NotBe(u1.Id);

        await using var db = h.NewContext();
        (await db.IdentityLogins.SingleAsync(l => l.ProviderSubject == "ms-oid")).UserId.Should().Be(orphanId);
    }

    [Fact]
    public async Task UpsertByLogin_does_NOT_repoint_when_target_email_login_is_unverified()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfUserStore(h.Factory);

        // The other user's login on the same email is UNVERIFIED, so there is no verified
        // target to repoint to — the orphan login stays put even though the incoming is verified.
        string u1Id;
        string orphanId;
        await using (var seed = h.NewContext())
        {
            var u1 = NewUser("owner@test");
            var u2 = NewUser("owner@test");
            u1Id = u1.Id;
            orphanId = u2.Id;
            seed.Users.AddRange(u1, u2);
            seed.IdentityLogins.Add(NewLoginRow(u1.Id, "local", "owner@test", "owner@test", false));
            seed.IdentityLogins.Add(NewLoginRow(u2.Id, "microsoft", "ms-oid", "owner@test", false));
            await seed.SaveChangesAsync();
        }

        var resolved = await store.UpsertByLoginAsync("microsoft", "ms-oid", "owner@test", true, "Owner MS");

        resolved.Id.Should().Be(orphanId);
        resolved.Id.Should().NotBe(u1Id);
    }

    [Fact]
    public async Task UpsertByLogin_throws_on_ambiguous_repoint_when_two_other_users_share_verified_email()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfUserStore(h.Factory);

        // Two OTHER users each own a verified login on the same email — repointing is ambiguous
        // and must be refused, never silently merged.
        await using (var seed = h.NewContext())
        {
            var u1 = NewUser("owner@test");
            var u2 = NewUser("owner@test");
            var orphan = NewUser("owner@test");
            seed.Users.AddRange(u1, u2, orphan);
            seed.IdentityLogins.Add(NewLoginRow(u1.Id, "local", "owner@test", "owner@test", true));
            seed.IdentityLogins.Add(NewLoginRow(u2.Id, "google", "g-1", "owner@test", true));
            seed.IdentityLogins.Add(NewLoginRow(orphan.Id, "microsoft", "ms-oid", "owner@test", false));
            await seed.SaveChangesAsync();
        }

        var act = () => store.UpsertByLoginAsync("microsoft", "ms-oid", "owner@test", true, "Owner MS");

        await act.Should().ThrowAsync<System.InvalidOperationException>()
            .WithMessage("*multiple users share verified email*");
    }

    [Fact]
    public async Task DeleteUser_removes_the_user_and_all_their_data_leaving_others_intact()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfUserStore(h.Factory);

        var alice = await store.UpsertByLoginAsync("microsoft", "oid-a", "alice@test", true, "Alice");
        var bob = await store.UpsertByLoginAsync("microsoft", "oid-b", "bob@test", true, "Bob");

        // Seed each of the cascade's deletion paths for BOTH users: UserId-scoped rows, parent-id-scoped
        // children (SyncRunLocks by PairId, PrefixRuleDestinations by RuleId), and the email-scoped magic link.
        await using (var db = h.NewContext())
        {
            db.Devices.Add(NewDevice(alice.Id));
            db.Devices.Add(NewDevice(bob.Id));
            db.ClipboardItems.Add(new() { Id = "c-a", UserId = alice.Id, OriginDeviceId = "d", CreatedUtc = System.DateTimeOffset.UtcNow });
            db.ClipboardItems.Add(new() { Id = "c-b", UserId = bob.Id, OriginDeviceId = "d", CreatedUtc = System.DateTimeOffset.UtcNow });
            db.SyncPairs.Add(new() { Id = "p-a", UserId = alice.Id, Name = "pair" });
            db.SyncRunLocks.Add(new() { PairId = "p-a" });
            db.PrefixRules.Add(new() { Id = "r-a", UserId = alice.Id, Prefix = "[x]", MaskTitle = "x" });
            db.PrefixRuleDestinations.Add(new() { Id = "rd-a", RuleId = "r-a", AccountId = "acc", CalendarId = "cal" });
            db.MagicLinks.Add(new() { Id = "m-a", TokenHash = "h", Email = "alice@test", Nonce = "n" });
            await db.SaveChangesAsync();
        }

        (await store.DeleteUserAsync(alice.Id)).Should().BeTrue();

        await using (var db = h.NewContext())
        {
            // Alice: gone everywhere, including the parent-id-scoped children and the magic link.
            (await db.Users.AnyAsync(u => u.Id == alice.Id)).Should().BeFalse();
            (await db.IdentityLogins.AnyAsync(l => l.UserId == alice.Id)).Should().BeFalse();
            (await db.Devices.AnyAsync(d => d.UserId == alice.Id)).Should().BeFalse();
            (await db.ClipboardItems.AnyAsync(c => c.UserId == alice.Id)).Should().BeFalse();
            (await db.SyncPairs.AnyAsync(p => p.UserId == alice.Id)).Should().BeFalse();
            (await db.SyncRunLocks.AnyAsync(l => l.PairId == "p-a")).Should().BeFalse();
            (await db.PrefixRules.AnyAsync(r => r.UserId == alice.Id)).Should().BeFalse();
            (await db.PrefixRuleDestinations.AnyAsync(d => d.RuleId == "r-a")).Should().BeFalse();
            (await db.MagicLinks.AnyAsync(m => m.Email == "alice@test")).Should().BeFalse();

            // Bob: untouched.
            (await db.Users.AnyAsync(u => u.Id == bob.Id)).Should().BeTrue();
            (await db.Devices.AnyAsync(d => d.UserId == bob.Id)).Should().BeTrue();
            (await db.ClipboardItems.AnyAsync(c => c.UserId == bob.Id)).Should().BeTrue();
        }
    }

    [Fact]
    public async Task DeleteUser_is_idempotent_returns_false_when_missing()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfUserStore(h.Factory);
        (await store.DeleteUserAsync("does-not-exist")).Should().BeFalse();
    }

    private static ZyncMaster.Server.Data.DeviceRow NewDevice(string userId)
    {
        var suffix = System.Guid.NewGuid().ToString("N")[..6];
        return new()
        {
            Id = System.Guid.NewGuid().ToString("N"),
            UserId = userId,
            Name = "Box-" + suffix,
            NameLower = ("box-" + suffix).ToLowerInvariant(),
            ApiKeyHash = System.Guid.NewGuid().ToString("N"),
            CreatedUtc = System.DateTimeOffset.UtcNow,
            LastSeenUtc = System.DateTimeOffset.UtcNow,
        };
    }
}
