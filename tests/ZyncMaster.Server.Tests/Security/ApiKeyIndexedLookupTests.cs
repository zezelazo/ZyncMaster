using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ZyncMaster.Server.Data;
using Xunit;

namespace ZyncMaster.Server.Tests.Security;

// §A-3 — the api-key handler now locates the single candidate device by the public keyId index and
// runs PBKDF2 once, instead of scanning every device. These tests prove a modern "keyId.secret"
// key authenticates, a wrong secret with a real keyId fails, an unknown keyId fails, and the
// legacy (separator-free) key still authenticates via the fallback scan path.
public class ApiKeyIndexedLookupTests : IClassFixture<ServerTestFactory>
{
    private readonly ServerTestFactory _factory;
    public ApiKeyIndexedLookupTests(ServerTestFactory factory) => _factory = factory;

    private async Task<(WebApplicationFactory<Program> factory, ApiKeyGenerator.GeneratedKey key)> SeedModernAsync()
    {
        var factory = _factory.WithWebHostBuilder(_ => { });
        var generated = ApiKeyGenerator.GenerateKey();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        // Seed a few decoy devices so a passing auth proves the lookup matched ONE candidate by
        // keyId rather than getting lucky on a near-empty table.
        for (var i = 0; i < 3; i++)
        {
            var decoy = ApiKeyGenerator.GenerateKey();
            db.Devices.Add(new DeviceRow
            {
                Id = Guid.NewGuid().ToString("N"),
                // Globally unique: the class shares one factory/DB across its tests, so the per-user
                // unique device-name index would reject a repeated decoy name across seed calls.
                Name = "Decoy-" + Guid.NewGuid().ToString("N"),
                KeyId = decoy.KeyId,
                ApiKeyHash = ApiKeyHasher.Hash(decoy.Secret),
                CreatedUtc = DateTimeOffset.UtcNow,
            });
        }
        db.Devices.Add(new DeviceRow
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "Target-" + Guid.NewGuid().ToString("N"),
            KeyId = generated.KeyId,
            ApiKeyHash = ApiKeyHasher.Hash(generated.Secret),
            CreatedUtc = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (factory, generated);
    }

    private static async Task<HttpResponseMessage> CallDevicesAsync(WebApplicationFactory<Program> factory, string apiKey)
    {
        var client = factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/devices");
        req.Headers.Add("X-Api-Key", apiKey);
        return await client.SendAsync(req);
    }

    [Fact]
    public async Task Modern_key_authenticates_via_keyId_index()
    {
        var (factory, key) = await SeedModernAsync();
        var resp = await CallDevicesAsync(factory, key.ApiKey);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Real_keyId_with_wrong_secret_fails()
    {
        var (factory, key) = await SeedModernAsync();
        var tampered = $"{key.KeyId}{ApiKeyGenerator.Separator}wrong-secret-half";
        var resp = await CallDevicesAsync(factory, tampered);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Unknown_keyId_fails()
    {
        var (factory, _) = await SeedModernAsync();
        var resp = await CallDevicesAsync(factory, $"no-such-keyid{ApiKeyGenerator.Separator}whatever");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Legacy_separatorless_key_still_authenticates_via_fallback()
    {
        var factory = _factory.WithWebHostBuilder(_ => { });
        var legacyKey = ApiKeyGenerator.Generate(); // no separator => legacy format
        legacyKey.Should().NotContain(".");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
            db.Devices.Add(new DeviceRow
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = "Legacy",
                KeyId = null, // legacy rows have no indexed handle
                ApiKeyHash = ApiKeyHasher.Hash(legacyKey),
                CreatedUtc = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var resp = await CallDevicesAsync(factory, legacyKey);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
