using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace ZyncMaster.Server.Tests.Sync;

// The /api/accounts projection (AccountInfo) has branchy normalization that the single-account
// happy path does not exercise:
//   * the literal "default" account key gets the friendly DisplayName "Connected account" and is
//     always flagged IsDefault;
//   * a lone real account is flagged IsDefault (list.Count == 1);
//   * when several real accounts are connected, NONE is auto-flagged default and each keeps its
//     UPN as its DisplayName.
public class AccountInfoNormalizationTests
{
    private static WebApplicationFactory<Program> NewFactory(CookieAuthHelper.FakeIdentityTokenService identity) =>
        new ServerTestFactory().WithFakeIdentity(identity);

    // Adds an extra connected account under the signed-in user's id (same user the OAuth
    // callback created), so /api/accounts returns more than one row for that user.
    private static async Task AddAccountForSignedInUserAsync(
        WebApplicationFactory<Program> factory, string subject, string upn, string display, string accountUpn)
    {
        var users = factory.Services.GetRequiredService<IUserStore>();
        var userId = (await users.UpsertAsync("microsoft", subject, upn, display)).Id;
        var accounts = factory.Services.GetRequiredService<IConnectedAccountStore>();
        await accounts.SetForUserAsync(userId, accountUpn, "rt-extra");
    }

    [Fact]
    public async Task Literal_default_account_is_named_connected_account_and_flagged_default()
    {
        // Empty UPN at connect time => the callback writes the account under the "default" key.
        var identity = new CookieAuthHelper.FakeIdentityTokenService
        {
            Subject = "oid-1",
            Upn = "",
            DisplayName = "Zeze",
        };
        var factory = NewFactory(identity);
        var client = await CookieAuthHelper.SignInAsync(factory);

        var accounts = await client.GetFromJsonAsync<JsonElement>("/api/accounts");

        accounts.GetArrayLength().Should().Be(1);
        accounts[0].GetProperty("accountRef").GetString().Should().Be("default");
        accounts[0].GetProperty("displayName").GetString().Should().Be("Connected account");
        accounts[0].GetProperty("isDefault").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Single_real_account_is_flagged_default_with_its_upn_as_name()
    {
        var identity = new CookieAuthHelper.FakeIdentityTokenService
        {
            Subject = "oid-2",
            Upn = "solo@test",
            DisplayName = "Solo",
        };
        var factory = NewFactory(identity);
        var client = await CookieAuthHelper.SignInAsync(factory);

        var accounts = await client.GetFromJsonAsync<JsonElement>("/api/accounts");

        accounts.GetArrayLength().Should().Be(1);
        accounts[0].GetProperty("accountRef").GetString().Should().Be("solo@test");
        accounts[0].GetProperty("displayName").GetString().Should().Be("solo@test");
        accounts[0].GetProperty("isDefault").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Multiple_real_accounts_have_no_auto_default_and_keep_their_upn_names()
    {
        var identity = new CookieAuthHelper.FakeIdentityTokenService
        {
            Subject = "oid-3",
            Upn = "primary@test",
            DisplayName = "Primary",
        };
        var factory = NewFactory(identity);
        var client = await CookieAuthHelper.SignInAsync(factory);

        // Connect a second real account under the same user.
        await AddAccountForSignedInUserAsync(factory, "oid-3", "primary@test", "Primary", "secondary@test");

        var accounts = await client.GetFromJsonAsync<JsonElement>("/api/accounts");

        accounts.GetArrayLength().Should().Be(2);
        var byRef = accounts.EnumerateArray().ToDictionary(
            a => a.GetProperty("accountRef").GetString()!,
            a => a);

        byRef.Keys.Should().BeEquivalentTo(new[] { "primary@test", "secondary@test" });
        // Neither is auto-flagged default (no literal "default" key, and count > 1).
        byRef.Values.Should().OnlyContain(a => a.GetProperty("isDefault").GetBoolean() == false);
        // Each keeps its own UPN as its display name.
        byRef["primary@test"].GetProperty("displayName").GetString().Should().Be("primary@test");
        byRef["secondary@test"].GetProperty("displayName").GetString().Should().Be("secondary@test");
    }
}
