using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace ZyncMaster.Server.Tests.Identity;

public class ConnectedAccountStoreTests
{
    private static DataProtectionConnectedAccountStore BuildStore() =>
        new(DataProtectionProvider.Create("tests"));

    [Fact]
    public async Task Set_then_GetRefreshToken_round_trips_plaintext()
    {
        var store = BuildStore();

        await store.SetAsync("user@test", "the-refresh-token");

        (await store.GetRefreshTokenAsync("user@test")).Should().Be("the-refresh-token");
    }

    [Fact]
    public async Task Stored_refresh_token_is_encrypted()
    {
        var store = BuildStore();

        await store.SetAsync("user@test", "the-refresh-token");
        var account = await store.GetAsync("user@test");

        account.Should().NotBeNull();
        account!.EncryptedRefreshToken.Should().NotBe("the-refresh-token");
        account.UserPrincipalName.Should().Be("user@test");
    }

    [Fact]
    public async Task GetRefreshToken_unknown_upn_returns_null()
    {
        var store = BuildStore();

        (await store.GetRefreshTokenAsync("nobody@test")).Should().BeNull();
    }

    [Fact]
    public async Task HasAny_is_false_when_empty_and_true_after_a_set()
    {
        var store = BuildStore();

        (await store.HasAnyAsync()).Should().BeFalse();

        await store.SetAsync("user@test", "rt");

        (await store.HasAnyAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task Null_or_empty_upn_uses_default_key_round_trip()
    {
        var store = BuildStore();

        await store.SetAsync("", "rt-default");

        (await store.GetRefreshTokenAsync("")).Should().Be("rt-default");
        (await store.GetRefreshTokenAsync("default")).Should().Be("rt-default");
    }

    [Fact]
    public async Task List_returns_all_connected_accounts()
    {
        var store = BuildStore();
        await store.SetAsync("alice@test", "rt-a");
        await store.SetAsync("bob@test", "rt-b");

        var accounts = await store.ListAsync();

        accounts.Select(a => a.UserPrincipalName)
            .Should().BeEquivalentTo(new[] { "alice@test", "bob@test" });
    }

    [Fact]
    public async Task List_is_empty_when_no_accounts()
    {
        var store = BuildStore();

        (await store.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Remove_deletes_account()
    {
        var store = BuildStore();
        await store.SetAsync("alice@test", "rt-a");

        await store.RemoveAsync("alice@test");

        (await store.GetAsync("alice@test")).Should().BeNull();
        (await store.HasAnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Remove_normalizes_empty_to_default_key()
    {
        var store = BuildStore();
        await store.SetAsync("", "rt-default");

        await store.RemoveAsync("");

        (await store.GetRefreshTokenAsync("default")).Should().BeNull();
    }
}
