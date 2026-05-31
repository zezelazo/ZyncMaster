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
}
