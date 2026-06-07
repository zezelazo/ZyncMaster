using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ZyncMaster.Server.Tests.Clipboard;

// Integration tests for the clipboard REST surface. Runs the real Program composition on the
// SQLite ServerTestFactory with the real EF stores, so the user-scoping filters are actually
// exercised. Users are minted through the real OAuth sign-in flow (switchable fake identity);
// device api keys are bound to a user's id the same way CrossUserIsolationTests / E2EHarness do.
//
// The WS live-push path is unit-tested at the broadcaster/registry level (Task 7) and is hard to
// assert through WebApplicationFactory, so it is not driven here; the REST routing, auth, scoping,
// File rejection and key-relay (no-persist) behaviour are covered.
public class ClipboardEndpointsTests
{
    // Per-test harness: real stores + the switchable identity so two distinct users can be created
    // against the same host and each bound to its own device api key.
    private sealed class Harness : IDisposable
    {
        public ServerTestFactory Inner { get; }
        public WebApplicationFactory<Program> Factory { get; }
        public CookieAuthHelper.FakeIdentityTokenService Identity { get; } = new();

        public Harness(long? hardMaxImageBytes = null)
        {
            Inner = new ServerTestFactory();
            var withIdentity = Inner.WithFakeIdentity(Identity);
            // Optionally shrink the clipboard image ceiling so a small decoded payload can trip
            // the HardMax guard. Chained last so this config wins over appsettings.
            Factory = hardMaxImageBytes is { } cap
                ? withIdentity.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, config) =>
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Clipboard:HardMaxImageBytes"] = cap.ToString(),
                    })))
                : withIdentity;
        }

        // Signs in the given identity through the real OAuth flow (creates the user row) and
        // returns the ZyncMaster user id for it.
        public async Task<string> SignInAndUserIdAsync(string subject, string upn, string display)
        {
            Identity.Subject = subject;
            Identity.Upn = upn;
            Identity.DisplayName = display;
            await CookieAuthHelper.SignInAsync(Factory);

            var users = Factory.Services.GetRequiredService<IUserStore>();
            var row = await users.UpsertAsync("microsoft", subject, upn, display);
            return row.Id;
        }

        // Binds a device to an explicit user id and returns its plaintext api key.
        public async Task<string> AddDeviceForUserAsync(string userId, string name = "Laptop")
        {
            var devices = Factory.Services.GetRequiredService<IDeviceStore>();
            var key = ApiKeyGenerator.Generate();
            await devices.AddAsync(new Device
            {
                Id = Guid.NewGuid().ToString("N"),
                UserId = userId,
                Name = name,
                ApiKeyHash = ApiKeyHasher.Hash(key),
                CreatedUtc = DateTimeOffset.UtcNow,
            });
            return key;
        }

        public HttpClient DeviceClient(string apiKey)
        {
            var client = Factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
            return client;
        }

        public HttpClient Anonymous() => Factory.CreateClient();

        public void Dispose()
        {
            Factory.Dispose();
            Inner.Dispose();
        }
    }

    private static object PublishBody(
        string id, string type, string originDeviceId, string payloadBase64,
        long? sizeBytes = null, string? thumbnailBase64 = null, string? preview = null) => new
    {
        id,
        type,
        originDeviceId,
        originDeviceName = "Laptop",
        sizeBytes,
        payloadBase64,
        thumbnailBase64,
        preview,
    };

    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Publish_then_History_returns_item()
    {
        using var h = new Harness();
        var userId = await h.SignInAndUserIdAsync("oid-a", "alice@test", "Alice");
        var device = h.DeviceClient(await h.AddDeviceForUserAsync(userId));

        var payload = B64("hello clipboard");
        var publish = await device.PostAsJsonAsync("/api/clipboard/items",
            PublishBody("item-1", "Text", "dev-a", payload));
        publish.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var pd = JsonDocument.Parse(await publish.Content.ReadAsStringAsync()))
            pd.RootElement.GetProperty("id").GetString().Should().Be("item-1");

        var history = await device.GetFromJsonAsync<JsonElement>("/api/clipboard/history");
        history.GetArrayLength().Should().Be(1);
        var first = history[0];
        first.GetProperty("id").GetString().Should().Be("item-1");
        first.GetProperty("type").GetString().Should().Be("Text");
        first.GetProperty("originDeviceId").GetString().Should().Be("dev-a");
        first.GetProperty("payloadBase64").GetString().Should().Be(payload);
    }

    [Fact]
    public async Task History_without_auth_returns_401()
    {
        using var h = new Harness();
        var resp = await h.Anonymous().GetAsync("/api/clipboard/history");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Publish_File_type_returns_400()
    {
        using var h = new Harness();
        var userId = await h.SignInAndUserIdAsync("oid-a", "alice@test", "Alice");
        var device = h.DeviceClient(await h.AddDeviceForUserAsync(userId));

        var resp = await device.PostAsJsonAsync("/api/clipboard/items",
            PublishBody("file-1", "File", "dev-a", B64("data"), sizeBytes: 4));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // Nothing was persisted for the rejected File item.
        var history = await device.GetFromJsonAsync<JsonElement>("/api/clipboard/history");
        history.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Settings_patch_then_list_roundtrips_and_is_user_scoped()
    {
        using var h = new Harness();
        var aUserId = await h.SignInAndUserIdAsync("oid-a", "alice@test", "Alice");
        var aDevice = h.DeviceClient(await h.AddDeviceForUserAsync(aUserId));

        var patch = await aDevice.PatchAsJsonAsync("/api/clipboard/settings/dev-a", new
        {
            autoSync = false,
            send = true,
            receive = false,
            viewerHotkey = "Ctrl+Alt+V",
            density = "mini",
            showHints = false,
        });
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        var aList = await aDevice.GetFromJsonAsync<JsonElement>("/api/clipboard/settings");
        aList.GetArrayLength().Should().Be(1);
        var row = aList[0];
        row.GetProperty("deviceId").GetString().Should().Be("dev-a");
        row.GetProperty("autoSync").GetBoolean().Should().BeFalse();
        row.GetProperty("receive").GetBoolean().Should().BeFalse();
        row.GetProperty("viewerHotkey").GetString().Should().Be("Ctrl+Alt+V");
        row.GetProperty("density").GetString().Should().Be("mini");
        row.GetProperty("showHints").GetBoolean().Should().BeFalse();

        // User B never sees user A's settings row.
        var bUserId = await h.SignInAndUserIdAsync("oid-b", "bob@test", "Bob");
        var bDevice = h.DeviceClient(await h.AddDeviceForUserAsync(bUserId));
        var bList = await bDevice.GetFromJsonAsync<JsonElement>("/api/clipboard/settings");
        bList.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Settings_patch_invalid_density_returns_400()
    {
        using var h = new Harness();
        var userId = await h.SignInAndUserIdAsync("oid-a", "alice@test", "Alice");
        var device = h.DeviceClient(await h.AddDeviceForUserAsync(userId));

        var resp = await device.PatchAsJsonAsync("/api/clipboard/settings/dev-a", new
        {
            autoSync = true,
            send = true,
            receive = true,
            viewerHotkey = "Ctrl+Win+Q",
            density = "huge", // invalid: only rich|mini allowed
            showHints = true,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task KeyRelay_to_offline_target_returns_delivered_false_and_persists_nothing()
    {
        using var h = new Harness();
        var userId = await h.SignInAndUserIdAsync("oid-a", "alice@test", "Alice");
        var device = h.DeviceClient(await h.AddDeviceForUserAsync(userId));

        var resp = await device.PostAsJsonAsync("/api/clipboard/key/relay", new
        {
            fromDeviceId = "dev-a",
            targetDeviceId = "dev-offline",
            wrappedKeyBase64 = B64("super-secret-wrapped-key"),
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        // No device of the user is connected over WS in this test, so delivery is false.
        doc.RootElement.GetProperty("delivered").GetBoolean().Should().BeFalse();

        // There is no key store at all; confirm the relay created no clipboard items (the only
        // user-visible persistence) — history is unaffected.
        var history = await device.GetFromJsonAsync<JsonElement>("/api/clipboard/history");
        history.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Cross_user_cannot_read_history()
    {
        using var h = new Harness();

        // User B publishes an item.
        var bUserId = await h.SignInAndUserIdAsync("oid-b", "bob@test", "Bob");
        var bDevice = h.DeviceClient(await h.AddDeviceForUserAsync(bUserId));
        (await bDevice.PostAsJsonAsync("/api/clipboard/items",
            PublishBody("b-item", "Text", "dev-b", B64("bob secret"))))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // User A's history does not include B's item.
        var aUserId = await h.SignInAndUserIdAsync("oid-a", "alice@test", "Alice");
        var aDevice = h.DeviceClient(await h.AddDeviceForUserAsync(aUserId));
        var aHistory = await aDevice.GetFromJsonAsync<JsonElement>("/api/clipboard/history");
        aHistory.GetArrayLength().Should().Be(0);

        // B still sees its own item.
        var bHistory = await bDevice.GetFromJsonAsync<JsonElement>("/api/clipboard/history");
        bHistory.GetArrayLength().Should().Be(1);
        bHistory[0].GetProperty("id").GetString().Should().Be("b-item");
    }

    [Fact]
    public async Task Publish_without_auth_returns_401()
    {
        using var h = new Harness();
        var resp = await h.Anonymous().PostAsJsonAsync("/api/clipboard/items",
            PublishBody("x", "Text", "dev-x", B64("nope")));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Publish_Image_over_hard_max_returns_413_even_when_client_understates_size()
    {
        // Hard ceiling is 8 bytes; the real decoded payload is 16 bytes but the client lies
        // (sizeBytes: 1). The server must reject on the ACTUAL byte count, not the claim.
        using var h = new Harness(hardMaxImageBytes: 8);
        var userId = await h.SignInAndUserIdAsync("oid-a", "alice@test", "Alice");
        var device = h.DeviceClient(await h.AddDeviceForUserAsync(userId));

        var payload = B64("0123456789abcdef"); // 16 bytes decoded
        var resp = await device.PostAsJsonAsync("/api/clipboard/items",
            PublishBody("img-big", "Image", "dev-a", payload, sizeBytes: 1));

        resp.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);

        // Nothing was stored for the rejected image.
        var history = await device.GetFromJsonAsync<JsonElement>("/api/clipboard/history");
        history.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Publish_Image_stores_actual_payload_length_not_client_size()
    {
        // Even within the ceiling, the server recomputes SizeBytes from the decoded payload and
        // ignores the client-supplied value (here a wildly understated 1).
        using var h = new Harness();
        var userId = await h.SignInAndUserIdAsync("oid-a", "alice@test", "Alice");
        var device = h.DeviceClient(await h.AddDeviceForUserAsync(userId));

        var payload = B64("0123456789abcdef"); // 16 bytes decoded
        var publish = await device.PostAsJsonAsync("/api/clipboard/items",
            PublishBody("img-1", "Image", "dev-a", payload, sizeBytes: 1));
        publish.StatusCode.Should().Be(HttpStatusCode.OK);

        var history = await device.GetFromJsonAsync<JsonElement>("/api/clipboard/history");
        history.GetArrayLength().Should().Be(1);
        history[0].GetProperty("sizeBytes").GetInt64().Should().Be(16);
    }
}
