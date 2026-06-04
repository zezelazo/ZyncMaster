using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using ZyncMaster.Server;
using ZyncMaster.Server.Data;
using ZyncMaster.Server.Tests.Storage;
using Xunit;

namespace ZyncMaster.Server.Tests.Calendar;

public class CalendarAccountStoreTests
{
    private sealed class FixedCurrentUser : ICurrentUserAccessor
    {
        public FixedCurrentUser(string userId) => UserId = userId;
        public string UserId { get; }
    }

    private static EfCalendarAccountStore BuildStore(EfStoreTestHarness h, string userId) =>
        new(h.Factory, new FixedCurrentUser(userId), DataProtectionProvider.Create("tests"));

    private static void SeedUser(EfStoreTestHarness h, string userId)
    {
        using var db = h.NewContext();
        db.Users.Add(new UserRow
        {
            Id = userId,
            Provider = "local",
            Subject = userId,
            DisplayName = userId,
            CreatedUtc = DateTimeOffset.UtcNow,
            PrimaryEmail = $"{userId}@test",
        });
        db.SaveChanges();
    }

    private static CalendarAccount GraphAccount(string userId, string id = "acc-1") => new()
    {
        Id = id,
        UserId = userId,
        Kind = AccountKind.Graph,
        Provider = "graph",
        AccountEmail = "calendar@test",
        Authority = "https://login.microsoftonline.com/common",
        Scope = AccountScope.Read,
        DisplayName = "Work Calendar",
        ConnectedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Add_then_Get_returns_account_and_GetRefreshToken_decrypts_original()
    {
        using var h = new EfStoreTestHarness();
        SeedUser(h, "user-1");
        var store = BuildStore(h, "user-1");

        var added = await store.AddAsync(GraphAccount("user-1"), "refresh-original");
        added.Id.Should().Be("acc-1");

        var fetched = await store.GetAsync("acc-1");
        fetched.Should().NotBeNull();
        fetched!.Kind.Should().Be(AccountKind.Graph);
        fetched.Provider.Should().Be("graph");
        fetched.Scope.Should().Be(AccountScope.Read);
        fetched.Authority.Should().Be("https://login.microsoftonline.com/common");

        (await store.GetRefreshTokenAsync("acc-1")).Should().Be("refresh-original");
    }

    [Fact]
    public async Task Stored_refresh_token_is_encrypted_at_rest()
    {
        using var h = new EfStoreTestHarness();
        SeedUser(h, "user-1");
        var store = BuildStore(h, "user-1");

        await store.AddAsync(GraphAccount("user-1"), "refresh-original");

        await using var db = h.NewContext();
        var row = db.CalendarAccounts.Single(a => a.Id == "acc-1");
        row.EncryptedRefreshToken.Should().NotBeNullOrEmpty();
        row.EncryptedRefreshToken.Should().NotBe("refresh-original");
    }

    [Fact]
    public async Task List_returns_only_current_user_accounts()
    {
        using var h = new EfStoreTestHarness();
        SeedUser(h, "user-1");
        SeedUser(h, "user-2");

        await BuildStore(h, "user-1").AddAsync(GraphAccount("user-1", "a1"), "t1");
        await BuildStore(h, "user-1").AddAsync(GraphAccount("user-1", "a2"), "t2");
        await BuildStore(h, "user-2").AddAsync(GraphAccount("user-2", "b1"), "t3");

        (await BuildStore(h, "user-1").ListAsync()).Select(a => a.Id)
            .Should().BeEquivalentTo(new[] { "a1", "a2" });
        (await BuildStore(h, "user-2").ListAsync()).Select(a => a.Id)
            .Should().BeEquivalentTo(new[] { "b1" });
    }

    [Fact]
    public async Task Get_and_GetRefreshToken_do_not_leak_across_users()
    {
        using var h = new EfStoreTestHarness();
        SeedUser(h, "user-1");
        SeedUser(h, "user-2");

        await BuildStore(h, "user-1").AddAsync(GraphAccount("user-1", "a1"), "t1");

        (await BuildStore(h, "user-2").GetAsync("a1")).Should().BeNull();
        (await BuildStore(h, "user-2").GetRefreshTokenAsync("a1")).Should().BeNull();
    }

    [Fact]
    public async Task UpdateRefreshToken_rotates_token()
    {
        using var h = new EfStoreTestHarness();
        SeedUser(h, "user-1");
        var store = BuildStore(h, "user-1");

        await store.AddAsync(GraphAccount("user-1"), "refresh-original");
        await store.UpdateRefreshTokenAsync("acc-1", "refresh-rotated");

        (await store.GetRefreshTokenAsync("acc-1")).Should().Be("refresh-rotated");
    }

    [Fact]
    public async Task UpdateRefreshToken_is_no_op_cross_user()
    {
        using var h = new EfStoreTestHarness();
        SeedUser(h, "user-1");
        SeedUser(h, "user-2");

        await BuildStore(h, "user-1").AddAsync(GraphAccount("user-1", "a1"), "original");
        await BuildStore(h, "user-2").UpdateRefreshTokenAsync("a1", "hijacked");

        (await BuildStore(h, "user-1").GetRefreshTokenAsync("a1")).Should().Be("original");
    }

    [Fact]
    public async Task UpgradeScope_changes_read_to_readwrite()
    {
        using var h = new EfStoreTestHarness();
        SeedUser(h, "user-1");
        var store = BuildStore(h, "user-1");

        await store.AddAsync(GraphAccount("user-1"), "t");
        await store.UpgradeScopeAsync("acc-1", AccountScope.ReadWrite);

        (await store.GetAsync("acc-1"))!.Scope.Should().Be(AccountScope.ReadWrite);
    }

    [Fact]
    public async Task UpdateStatus_changes_status()
    {
        using var h = new EfStoreTestHarness();
        SeedUser(h, "user-1");
        var store = BuildStore(h, "user-1");

        await store.AddAsync(GraphAccount("user-1"), "t");
        await store.UpdateStatusAsync("acc-1", "revoked");

        (await store.GetAsync("acc-1"))!.Status.Should().Be("revoked");
    }

    [Fact]
    public async Task Remove_deletes_account_and_token()
    {
        using var h = new EfStoreTestHarness();
        SeedUser(h, "user-1");
        var store = BuildStore(h, "user-1");

        await store.AddAsync(GraphAccount("user-1"), "t");
        await store.RemoveAsync("acc-1");

        (await store.GetAsync("acc-1")).Should().BeNull();
        (await store.GetRefreshTokenAsync("acc-1")).Should().BeNull();
    }

    [Fact]
    public async Task Remove_is_no_op_cross_user()
    {
        using var h = new EfStoreTestHarness();
        SeedUser(h, "user-1");
        SeedUser(h, "user-2");

        await BuildStore(h, "user-1").AddAsync(GraphAccount("user-1", "a1"), "t");
        await BuildStore(h, "user-2").RemoveAsync("a1");

        (await BuildStore(h, "user-1").GetAsync("a1")).Should().NotBeNull();
    }

    [Fact]
    public async Task Add_outlook_com_stores_no_token_and_keeps_device_id()
    {
        using var h = new EfStoreTestHarness();
        SeedUser(h, "user-1");
        var store = BuildStore(h, "user-1");

        var account = new CalendarAccount
        {
            Id = "dev-acc",
            UserId = "user-1",
            Kind = AccountKind.OutlookCom,
            Provider = "outlook-com",
            AccountEmail = "legacy@outlook.com",
            Scope = AccountScope.ReadWrite,
            DeviceId = "device-123",
            ConnectedAt = DateTimeOffset.UtcNow,
        };

        await store.AddAsync(account, refreshToken: null);

        var fetched = await store.GetAsync("dev-acc");
        fetched.Should().NotBeNull();
        fetched!.Kind.Should().Be(AccountKind.OutlookCom);
        fetched.DeviceId.Should().Be("device-123");

        (await store.GetRefreshTokenAsync("dev-acc")).Should().BeNull();
    }
}
