using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ZyncMaster.Server;
using ZyncMaster.Server.Data;
using Xunit;

namespace ZyncMaster.Server.Tests.Identity;

// Integration coverage of POST /identity/refresh (plan v2 §A-1) over the SQLite-backed
// ServerTestFactory. The endpoint is anonymous — the refresh token is the proof — and rotates
// the token on every redeem, so a replay of the old token must fail. Uses the real
// IIdentityTokenService so the issue→redeem→rotate ledger is exercised end to end.
public class IdentityRefreshEndpointTests
{
    private static async Task<(ServerTestFactory factory, HttpClient client, string refreshToken, string userId)>
        SeedUserWithRefreshAsync()
    {
        var factory = new ServerTestFactory();
        // Touch Services so the test host (and its DI graph) is built before we resolve stores.
        var tokens = factory.Services.GetRequiredService<IIdentityTokenService>();
        var dbFactory = factory.Services
            .GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<ZyncMasterDbContext>>();

        var user = new UserRow
        {
            Id = Guid.NewGuid().ToString("N"),
            Provider = "local",
            Subject = "subj@test",
            Email = "subj@test",
            DisplayName = "Subject",
            PrimaryEmail = "subj@test",
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        var refresh = await tokens.IssueRefreshTokenAsync(user.Id);
        return (factory, factory.CreateClient(), refresh, user.Id);
    }

    [Fact]
    public async Task Refresh_with_valid_token_returns_new_access_and_rotated_refresh()
    {
        var (factory, client, refresh, _) = await SeedUserWithRefreshAsync();
        using var _f = factory;

        var resp = await client.PostAsJsonAsync("/identity/refresh", new { refreshToken = refresh });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var accessToken = doc.RootElement.GetProperty("accessToken").GetString();
        var newRefresh = doc.RootElement.GetProperty("newRefreshToken").GetString();

        accessToken.Should().NotBeNullOrEmpty();
        newRefresh.Should().NotBeNullOrEmpty();
        newRefresh.Should().NotBe(refresh);

        // The freshly minted access token validates against the live token service.
        var tokens = factory.Services.GetRequiredService<IIdentityTokenService>();
        tokens.ValidateAccessToken(accessToken!).Should().NotBeNull();
    }

    [Fact]
    public async Task Refresh_rotates_so_old_token_no_longer_works()
    {
        var (factory, client, refresh, _) = await SeedUserWithRefreshAsync();
        using var _f = factory;

        // First redeem succeeds and rotates the token.
        var first = await client.PostAsJsonAsync("/identity/refresh", new { refreshToken = refresh });
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        // Replaying the OLD token must now be rejected (rotation defeats replay).
        var replay = await client.PostAsJsonAsync("/identity/refresh", new { refreshToken = refresh });
        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_with_rotated_token_works_once_more()
    {
        var (factory, client, refresh, _) = await SeedUserWithRefreshAsync();
        using var _f = factory;

        var first = await client.PostAsJsonAsync("/identity/refresh", new { refreshToken = refresh });
        using var firstDoc = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var rotated = firstDoc.RootElement.GetProperty("newRefreshToken").GetString();

        var second = await client.PostAsJsonAsync("/identity/refresh", new { refreshToken = rotated });
        second.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Refresh_with_invalid_token_returns_401()
    {
        var factory = new ServerTestFactory();
        using var _f = factory;
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/identity/refresh", new { refreshToken = "never-issued" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_with_missing_token_returns_400()
    {
        var factory = new ServerTestFactory();
        using var _f = factory;
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/identity/refresh", new { });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
