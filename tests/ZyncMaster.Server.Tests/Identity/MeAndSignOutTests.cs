using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace ZyncMaster.Server.Tests.Identity;

public class MeAndSignOutTests
{
    private static WebApplicationFactory<Program> Build(CookieAuthHelper.FakeIdentityTokenService fake) =>
        new ServerTestFactory().WithFakeIdentity(fake);

    [Fact]
    public async Task Me_requires_cookie()
    {
        var factory = new ServerTestFactory().WithWebHostBuilder(_ => { });
        var resp = await factory.CreateClient().GetAsync("/api/me");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_rejects_api_key()
    {
        var factory = new ServerTestFactory().WithWebHostBuilder(_ => { });
        var deviceStore = factory.Services.GetRequiredService<IDeviceStore>();
        var key = ApiKeyGenerator.Generate();
        await deviceStore.AddAsync(new Device
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Laptop",
            ApiKeyHash = ApiKeyHasher.Hash(key),
            CreatedUtc = DateTimeOffset.UtcNow,
        });
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);

        (await client.GetAsync("/api/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_returns_current_user_profile_with_cookie()
    {
        var fake = new CookieAuthHelper.FakeIdentityTokenService
        {
            Subject = "oid-me",
            Upn = "me@test",
            DisplayName = "Me Tester",
        };
        var factory = Build(fake);
        var client = await CookieAuthHelper.SignInAsync(factory);

        var resp = await client.GetAsync("/api/me");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("email").GetString().Should().Be("me@test");
        doc.RootElement.GetProperty("displayName").GetString().Should().Be("Me Tester");
    }

    [Fact]
    public async Task SignOut_clears_cookie_and_redirects_home()
    {
        var fake = new CookieAuthHelper.FakeIdentityTokenService();
        var factory = Build(fake);
        var client = await CookieAuthHelper.SignInAsync(factory);

        // Authenticated before sign-out.
        (await client.GetAsync("/api/me")).StatusCode.Should().Be(HttpStatusCode.OK);

        var signOut = await client.PostAsync("/signout", content: null);
        signOut.StatusCode.Should().Be(HttpStatusCode.Redirect);
        signOut.Headers.Location!.ToString().Should().Be("/");

        // The Set-Cookie on sign-out expires the session cookie; the same client is now
        // unauthenticated for the cookie-gated endpoint.
        (await client.GetAsync("/api/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
