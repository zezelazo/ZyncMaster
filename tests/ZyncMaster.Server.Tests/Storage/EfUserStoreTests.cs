using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace ZyncMaster.Server.Tests.Storage;

public class EfUserStoreTests
{
    [Fact]
    public async Task Upsert_creates_user_when_absent()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfUserStore(h.Factory);

        var user = await store.UpsertAsync("microsoft", "oid-1", "a@test", "Alice");

        user.Id.Should().NotBeNullOrEmpty();
        user.Provider.Should().Be("microsoft");
        user.Subject.Should().Be("oid-1");
        user.Email.Should().Be("a@test");
        user.DisplayName.Should().Be("Alice");
    }

    [Fact]
    public async Task Upsert_updates_email_and_name_on_existing_subject()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfUserStore(h.Factory);

        var first = await store.UpsertAsync("microsoft", "oid-1", "old@test", "Old Name");
        var second = await store.UpsertAsync("microsoft", "oid-1", "new@test", "New Name");

        // Same identity (provider+subject) => same row, refreshed profile.
        second.Id.Should().Be(first.Id);
        second.Email.Should().Be("new@test");
        second.DisplayName.Should().Be("New Name");
    }

    [Fact]
    public async Task Different_subject_creates_a_distinct_user()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfUserStore(h.Factory);

        var a = await store.UpsertAsync("microsoft", "oid-1", "a@test", "A");
        var b = await store.UpsertAsync("microsoft", "oid-2", "b@test", "B");

        b.Id.Should().NotBe(a.Id);
    }

    [Fact]
    public async Task Get_returns_user_by_id_and_null_when_missing()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfUserStore(h.Factory);
        var created = await store.UpsertAsync("microsoft", "oid-1", "a@test", "A");

        (await store.GetAsync(created.Id))!.Subject.Should().Be("oid-1");
        (await store.GetAsync("does-not-exist")).Should().BeNull();
    }

    [Fact]
    public async Task Get_resolves_the_seeded_default_user()
    {
        using var h = new EfStoreTestHarness();
        var store = new EfUserStore(h.Factory);

        var user = await store.GetAsync(DefaultCurrentUserAccessor.DefaultUserId);

        user.Should().NotBeNull();
        user!.Provider.Should().Be("local");
    }
}
