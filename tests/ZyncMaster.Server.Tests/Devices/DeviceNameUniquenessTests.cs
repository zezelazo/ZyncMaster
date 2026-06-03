using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ZyncMaster.Server.Data;
using Xunit;

namespace ZyncMaster.Server.Tests.Devices;

// Backend hardening of per-user device-name uniqueness (case-insensitive on both providers via the
// derived NameLower column + unique (UserId, NameLower) index), the register collision retry, the
// rename "name_taken" rejection, and the live availability endpoint.
public class DeviceNameUniquenessTests
{
    private static (string token, string userId) IssueToken(
        WebApplicationFactory<Program> factory, string subject, string email)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserStore>();
        var tokens = scope.ServiceProvider.GetRequiredService<IIdentityTokenService>();
        var user = users.UpsertByLoginAsync("local", subject, email, true, "Uniq User", CancellationToken.None)
            .GetAwaiter().GetResult();
        return (tokens.IssueAccessToken(user).Token, user.Id);
    }

    private static async Task<(string apiKey, string deviceId)> RegisterDevice(
        HttpClient client, string token, string name)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/devices/register")
        {
            Content = JsonContent.Create(new { name }),
        };
        req.Headers.Add("Authorization", $"Bearer {token}");
        var resp = await client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("apiKey").GetString()!,
                doc.RootElement.GetProperty("deviceId").GetString()!);
    }

    private static HttpRequestMessage RenameReq(string apiKey, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/devices/rename")
        {
            Content = JsonContent.Create(body),
        };
        req.Headers.Add("X-Api-Key", apiKey);
        return req;
    }

    private static async Task<HttpResponseMessage> NameAvailable(HttpClient client, string apiKey, string name)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/devices/name-available?name={Uri.EscapeDataString(name)}");
        req.Headers.Add("X-Api-Key", apiKey);
        return await client.SendAsync(req);
    }

    // ---------------- Unique index ----------------

    [Fact]
    public async Task Unique_index_blocks_two_devices_with_same_name_for_same_user()
    {
        using var factory = new ServerTestFactory();
        var (_, userId) = IssueToken(factory, "uniq-1", "uniq1@example.com");

        // Insert two rows directly with the same (UserId, NameLower) — the unique index must reject.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        db.Devices.Add(new DeviceRow { Id = "d1", UserId = userId, Name = "Frodo", NameLower = "frodo", ApiKeyHash = "h1" });
        db.Devices.Add(new DeviceRow { Id = "d2", UserId = userId, Name = "frodo", NameLower = "frodo", ApiKeyHash = "h2" });

        Func<Task> act = () => db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Same_name_allowed_across_different_users()
    {
        using var factory = new ServerTestFactory();
        var (_, u1) = IssueToken(factory, "uniq-a", "a@example.com");
        var (_, u2) = IssueToken(factory, "uniq-b", "b@example.com");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        db.Devices.Add(new DeviceRow { Id = "d1", UserId = u1, Name = "Neo", NameLower = "neo", ApiKeyHash = "h1" });
        db.Devices.Add(new DeviceRow { Id = "d2", UserId = u2, Name = "Neo", NameLower = "neo", ApiKeyHash = "h2" });

        Func<Task> act = () => db.SaveChangesAsync();

        await act.Should().NotThrowAsync();
    }

    // ---------------- Register retry ----------------

    [Fact]
    public async Task Register_without_name_twice_produces_distinct_names_despite_unique_index()
    {
        // The generator + the unique index must coexist: two nameless registrations for the same
        // user yield two distinct, persisted names (the retry path handles any collision).
        using var factory = new ServerTestFactory();
        var (token, _) = IssueToken(factory, "uniq-gen", "twins@example.com");
        var client = factory.CreateClient();

        var (_, id1) = await RegisterDevice(client, token, "");
        var (_, id2) = await RegisterDevice(client, token, "");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ZyncMasterDbContext>();
        var n1 = (await db.Devices.FirstAsync(d => d.Id == id1)).Name;
        var n2 = (await db.Devices.FirstAsync(d => d.Id == id2)).Name;
        string.Equals(n1, n2, StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    [Fact]
    public async Task Register_with_explicit_name_already_taken_is_rejected()
    {
        using var factory = new ServerTestFactory();
        var (token, _) = IssueToken(factory, "uniq-exp", "exp@example.com");
        var client = factory.CreateClient();
        await RegisterDevice(client, token, "Workstation");

        // A second explicit registration with the same name (different case) must NOT silently
        // duplicate — the service surfaces name_taken, which the endpoint maps to a non-2xx.
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/devices/register")
        {
            Content = JsonContent.Create(new { name = "workstation" }),
        };
        req.Headers.Add("Authorization", $"Bearer {token}");
        var resp = await client.SendAsync(req);

        resp.IsSuccessStatusCode.Should().BeFalse();
    }

    // ---------------- Rename ----------------

    [Fact]
    public async Task Rename_to_name_used_by_another_device_returns_409_name_taken()
    {
        using var factory = new ServerTestFactory();
        var (token, _) = IssueToken(factory, "uniq-rn", "rn@example.com");
        var client = factory.CreateClient();
        var (apiKeyA, _) = await RegisterDevice(client, token, "Device A");
        await RegisterDevice(client, token, "Device B");

        // Device A tries to take Device B's name (different case) — rejected.
        var resp = await client.SendAsync(RenameReq(apiKeyA, new { name = "device b" }));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetString().Should().Be("name_taken");
    }

    [Fact]
    public async Task Rename_to_own_current_name_is_ok()
    {
        using var factory = new ServerTestFactory();
        var (token, _) = IssueToken(factory, "uniq-self", "self@example.com");
        var client = factory.CreateClient();
        var (apiKey, deviceId) = await RegisterDevice(client, token, "Keeper");

        var resp = await client.SendAsync(RenameReq(apiKey, new { name = "Keeper" }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("deviceId").GetString().Should().Be(deviceId);
        doc.RootElement.GetProperty("name").GetString().Should().Be("Keeper");
    }

    [Fact]
    public async Task Rename_to_free_name_succeeds()
    {
        using var factory = new ServerTestFactory();
        var (token, _) = IssueToken(factory, "uniq-free", "free@example.com");
        var client = factory.CreateClient();
        var (apiKey, _) = await RegisterDevice(client, token, "Old Name");

        var resp = await client.SendAsync(RenameReq(apiKey, new { name = "Brand New" }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("name").GetString().Should().Be("Brand New");
    }

    // ---------------- name-available endpoint ----------------

    [Fact]
    public async Task NameAvailable_true_for_a_free_name()
    {
        using var factory = new ServerTestFactory();
        var (token, _) = IssueToken(factory, "av-free", "avfree@example.com");
        var client = factory.CreateClient();
        var (apiKey, _) = await RegisterDevice(client, token, "Mine");

        var resp = await NameAvailable(client, apiKey, "Totally Free");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("available").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task NameAvailable_false_when_taken_by_another_device()
    {
        using var factory = new ServerTestFactory();
        var (token, _) = IssueToken(factory, "av-taken", "avtaken@example.com");
        var client = factory.CreateClient();
        var (apiKeyA, _) = await RegisterDevice(client, token, "Device A");
        await RegisterDevice(client, token, "Device B");

        // Case-insensitive: "DEVICE B" collides with "Device B".
        var resp = await NameAvailable(client, apiKeyA, "DEVICE B");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("available").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task NameAvailable_true_for_caller_own_current_name()
    {
        using var factory = new ServerTestFactory();
        var (token, _) = IssueToken(factory, "av-self", "avself@example.com");
        var client = factory.CreateClient();
        var (apiKey, _) = await RegisterDevice(client, token, "My Laptop");

        // Re-typing the caller's own current name reports available (the caller's device is excluded).
        var resp = await NameAvailable(client, apiKey, "My Laptop");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("available").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task NameAvailable_invalid_for_blank_and_too_long()
    {
        using var factory = new ServerTestFactory();
        var (token, _) = IssueToken(factory, "av-inv", "avinv@example.com");
        var client = factory.CreateClient();
        var (apiKey, _) = await RegisterDevice(client, token, "Anchor");

        foreach (var bad in new[] { "   ", new string('x', 101) })
        {
            var resp = await NameAvailable(client, apiKey, bad);
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("available").GetBoolean().Should().BeFalse();
            doc.RootElement.GetProperty("reason").GetString().Should().Be("invalid");
        }
    }

    [Fact]
    public async Task NameAvailable_without_api_key_returns_401()
    {
        using var factory = new ServerTestFactory();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/devices/name-available?name=anything");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
