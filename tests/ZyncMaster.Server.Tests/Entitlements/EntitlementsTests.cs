using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ZyncMaster.Server.Tests.Entitlements;

// End-to-end tests for the entitlements endpoints (Track C), exercised through the real Program
// composition + RequireIdentityBearer. Bearers come from the real IIdentityTokenService.
public sealed class EntitlementsTests
{
    private static (string token, string userId) IssueBearer(
        WebApplicationFactory<Program> factory, string subject, string email)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserStore>();
        var tokens = scope.ServiceProvider.GetRequiredService<IIdentityTokenService>();
        var user = users.UpsertByLoginAsync(
            "local", subject, email, emailVerified: true, displayName: subject, CancellationToken.None)
            .GetAwaiter().GetResult();
        return (tokens.IssueAccessToken(user).Token, user.Id);
    }

    private static HttpRequestMessage Bearer(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage resp)
    {
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Get_without_bearer_is_unauthorized()
    {
        using var factory = new ServerTestFactory();
        var resp = await factory.CreateClient().GetAsync("/api/entitlements");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_returns_defaults_when_no_toggle()
    {
        using var factory = new ServerTestFactory();
        var (token, _) = IssueBearer(factory, "ent-user-1", "e1@test");

        var resp = await factory.CreateClient().SendAsync(Bearer(HttpMethod.Get, "/api/entitlements", token));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await ReadJson(resp);
        json.GetProperty("cloudFallbackSync").GetBoolean().Should().BeTrue();
        json.GetProperty("maxPairs").GetInt32().Should().Be(int.MaxValue);
        json.GetProperty("maxConnectedAccounts").GetInt32().Should().Be(int.MaxValue);
        json.GetProperty("minSyncIntervalMinutes").GetInt32().Should().Be(1);
        json.GetProperty("enabledModules").GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task Patch_persists_toggle_off_and_get_reflects_it()
    {
        using var factory = new ServerTestFactory();
        var (token, _) = IssueBearer(factory, "ent-user-2", "e2@test");
        var client = factory.CreateClient();

        var patch = Bearer(HttpMethod.Patch, "/api/entitlements/toggles", token);
        patch.Content = JsonContent.Create(new { cloudFallbackSync = false });
        var patchResp = await client.SendAsync(patch);
        patchResp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJson(patchResp)).GetProperty("cloudFallbackSync").GetBoolean().Should().BeFalse();

        var getResp = await client.SendAsync(Bearer(HttpMethod.Get, "/api/entitlements", token));
        var json = await ReadJson(getResp);
        json.GetProperty("cloudFallbackSync").GetBoolean().Should().BeFalse();
        // The rest stays unlocked — only the cloud-fallback lever was flipped.
        json.GetProperty("maxPairs").GetInt32().Should().Be(int.MaxValue);
        json.GetProperty("minSyncIntervalMinutes").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Patch_can_toggle_back_on()
    {
        using var factory = new ServerTestFactory();
        var (token, _) = IssueBearer(factory, "ent-user-3", "e3@test");
        var client = factory.CreateClient();

        var off = Bearer(HttpMethod.Patch, "/api/entitlements/toggles", token);
        off.Content = JsonContent.Create(new { cloudFallbackSync = false });
        await client.SendAsync(off);

        var on = Bearer(HttpMethod.Patch, "/api/entitlements/toggles", token);
        on.Content = JsonContent.Create(new { cloudFallbackSync = true });
        await client.SendAsync(on);

        var getResp = await client.SendAsync(Bearer(HttpMethod.Get, "/api/entitlements", token));
        (await ReadJson(getResp)).GetProperty("cloudFallbackSync").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Toggles_are_isolated_per_user()
    {
        using var factory = new ServerTestFactory();
        var (tokenA, _) = IssueBearer(factory, "ent-user-a", "a@test");
        var (tokenB, _) = IssueBearer(factory, "ent-user-b", "b@test");
        var client = factory.CreateClient();

        // A turns cloud fallback off.
        var patch = Bearer(HttpMethod.Patch, "/api/entitlements/toggles", tokenA);
        patch.Content = JsonContent.Create(new { cloudFallbackSync = false });
        await client.SendAsync(patch);

        // B is unaffected — still the unlocked default.
        var getB = await client.SendAsync(Bearer(HttpMethod.Get, "/api/entitlements", tokenB));
        (await ReadJson(getB)).GetProperty("cloudFallbackSync").GetBoolean().Should().BeTrue();

        // A is off.
        var getA = await client.SendAsync(Bearer(HttpMethod.Get, "/api/entitlements", tokenA));
        (await ReadJson(getA)).GetProperty("cloudFallbackSync").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Patch_with_missing_field_is_bad_request()
    {
        using var factory = new ServerTestFactory();
        var (token, _) = IssueBearer(factory, "ent-user-4", "e4@test");

        var patch = Bearer(HttpMethod.Patch, "/api/entitlements/toggles", token);
        patch.Content = JsonContent.Create(new { });
        var resp = await factory.CreateClient().SendAsync(patch);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
