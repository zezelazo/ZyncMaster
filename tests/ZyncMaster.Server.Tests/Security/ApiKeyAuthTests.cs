using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ZyncMaster.Server.Tests.Security;

public class ApiKeyAuthTests : IClassFixture<ServerTestFactory>
{
    private readonly ServerTestFactory _factory;

    public ApiKeyAuthTests(ServerTestFactory factory) => _factory = factory;

    private async Task<(WebApplicationFactory<Program> factory, string key, string deviceId)> SeedDeviceAsync()
    {
        var factory = _factory.WithWebHostBuilder(_ => { });
        var store = factory.Services.GetRequiredService<IDeviceStore>();

        var key = ApiKeyGenerator.Generate();
        var device = new Device
        {
            Id = Guid.NewGuid().ToString("N"),
            // Unique per seed: the class shares one ServerTestFactory (IClassFixture) and therefore
            // one database + the default user, so a fixed name would collide on the per-user unique
            // device-name index across the class's tests.
            Name = "Seeded-" + Guid.NewGuid().ToString("N"),
            ApiKeyHash = ApiKeyHasher.Hash(key),
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        await store.AddAsync(device);
        return (factory, key, device.Id);
    }

    [Fact]
    public async Task No_header_returns_401()
    {
        var (factory, _, _) = await SeedDeviceAsync();
        var resp = await factory.CreateClient().GetAsync("/api/devices");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Wrong_key_returns_401()
    {
        var (factory, _, _) = await SeedDeviceAsync();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "totally-wrong-key");
        var resp = await client.GetAsync("/api/devices");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Correct_key_returns_200_and_updates_last_seen()
    {
        var (factory, key, deviceId) = await SeedDeviceAsync();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);

        var resp = await client.GetAsync("/api/devices");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var store = factory.Services.GetRequiredService<IDeviceStore>();
        var device = await store.GetAsync(deviceId);
        device.Should().NotBeNull();
        device!.LastSeenUtc.Should().NotBeNull();
    }
}
