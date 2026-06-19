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
    public async Task Publish_File_without_a_name_returns_400()
    {
        using var h = new Harness();
        var userId = await h.SignInAndUserIdAsync("oid-a", "alice@test", "Alice");
        var device = h.DeviceClient(await h.AddDeviceForUserAsync(userId));

        // A File must carry its name (preview) + size; one without a name is rejected.
        var resp = await device.PostAsJsonAsync("/api/clipboard/items",
            PublishBody("file-1", "File", "dev-a", "", sizeBytes: 4));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var history = await device.GetFromJsonAsync<JsonElement>("/api/clipboard/history");
        history.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Publish_File_metadata_is_accepted_and_listed()
    {
        using var h = new Harness();
        var userId = await h.SignInAndUserIdAsync("oid-a", "alice@test", "Alice");
        var device = h.DeviceClient(await h.AddDeviceForUserAsync(userId));

        // A File carries only metadata here (name in preview, size); the bytes go via the blob endpoint.
        var resp = await device.PostAsJsonAsync("/api/clipboard/items",
            PublishBody("file-1", "File", "dev-a", "", sizeBytes: 4096, preview: "report.pdf"));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var history = await device.GetFromJsonAsync<JsonElement>("/api/clipboard/history");
        history.GetArrayLength().Should().Be(1);
        history[0].GetProperty("type").GetString().Should().Be("File");
        history[0].GetProperty("preview").GetString().Should().Be("report.pdf");
        history[0].GetProperty("sizeBytes").GetInt64().Should().Be(4096);
    }

    [Fact]
    public async Task Blob_upload_then_download_round_trips()
    {
        using var h = new Harness();
        var userId = await h.SignInAndUserIdAsync("oid-a", "alice@test", "Alice");
        var device = h.DeviceClient(await h.AddDeviceForUserAsync(userId));

        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        var up = await device.PostAsync("/api/clipboard/blobs/file-1", new ByteArrayContent(bytes));
        up.StatusCode.Should().Be(HttpStatusCode.OK);

        var down = await device.GetAsync("/api/clipboard/blobs/file-1");
        down.StatusCode.Should().Be(HttpStatusCode.OK);
        (await down.Content.ReadAsByteArrayAsync()).Should().Equal(bytes);
    }

    [Fact]
    public async Task Blob_download_missing_returns_404()
    {
        using var h = new Harness();
        var userId = await h.SignInAndUserIdAsync("oid-a", "alice@test", "Alice");
        var device = h.DeviceClient(await h.AddDeviceForUserAsync(userId));

        (await device.GetAsync("/api/clipboard/blobs/never-uploaded")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Blob_upload_without_auth_returns_401()
    {
        using var h = new Harness();
        var resp = await h.Anonymous().PostAsync("/api/clipboard/blobs/file-1", new ByteArrayContent(new byte[] { 1 }));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Blob_is_user_scoped_other_user_gets_404()
    {
        using var h = new Harness();
        var aId = await h.SignInAndUserIdAsync("oid-a", "alice@test", "Alice");
        var alice = h.DeviceClient(await h.AddDeviceForUserAsync(aId));
        await alice.PostAsync("/api/clipboard/blobs/file-1", new ByteArrayContent(new byte[] { 9 }));

        var bId = await h.SignInAndUserIdAsync("oid-b", "bob@test", "Bob");
        var bob = h.DeviceClient(await h.AddDeviceForUserAsync(bId, "Bob-Laptop"));
        (await bob.GetAsync("/api/clipboard/blobs/file-1")).StatusCode.Should().Be(HttpStatusCode.NotFound);
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
    public async Task Settings_get_by_device_returns_that_devices_settings_and_defaults_for_unknown()
    {
        // The App's HttpWsClipboardTransport.GetSettingsAsync calls GET /api/clipboard/settings/{deviceId}
        // for THIS device (clipboard startup) and for every device in the roster. The route was missing
        // (only GET /settings (list) + PATCH /settings/{id} existed), so the App got 405 and the whole
        // clipboard pipeline aborted before registering the hotkey or connecting. This pins the route.
        using var h = new Harness();
        var userId = await h.SignInAndUserIdAsync("oid-a", "alice@test", "Alice");
        var device = h.DeviceClient(await h.AddDeviceForUserAsync(userId));

        var patch = await device.PatchAsJsonAsync("/api/clipboard/settings/dev-a", new
        {
            autoSync = false,
            send = true,
            receive = false,
            viewerHotkey = "Ctrl+Alt+V",
            density = "mini",
            showHints = false,
        });
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        // A single device's settings — 200 with a single object (NOT 405, NOT an array).
        var resp = await device.GetAsync("/api/clipboard/settings/dev-a");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await resp.Content.ReadFromJsonAsync<JsonElement>();
        dto.GetProperty("deviceId").GetString().Should().Be("dev-a");
        dto.GetProperty("autoSync").GetBoolean().Should().BeFalse();
        dto.GetProperty("receive").GetBoolean().Should().BeFalse();
        dto.GetProperty("viewerHotkey").GetString().Should().Be("Ctrl+Alt+V");
        dto.GetProperty("density").GetString().Should().Be("mini");

        // An unknown/never-PATCHed device returns DEFAULTS (200), not 404 — the common first-run case.
        var def = await device.GetAsync("/api/clipboard/settings/never-set");
        def.StatusCode.Should().Be(HttpStatusCode.OK);
        var defDto = await def.Content.ReadFromJsonAsync<JsonElement>();
        defDto.GetProperty("deviceId").GetString().Should().Be("never-set");
    }

    // Capturing socket: records each text frame sent and reports Open so the broadcaster treats it
    // as live. Mirrors the one in ClipboardBroadcasterTests; kept local so this file is self-contained.
    private sealed class CapturingWebSocket : System.Net.WebSockets.WebSocket
    {
        public List<string> Sent { get; } = new();
        public override System.Net.WebSockets.WebSocketState State => System.Net.WebSockets.WebSocketState.Open;
        public override Task SendAsync(ArraySegment<byte> buffer, System.Net.WebSockets.WebSocketMessageType messageType, bool endOfMessage, System.Threading.CancellationToken cancellationToken)
        {
            Sent.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
            return Task.CompletedTask;
        }
        public override System.Net.WebSockets.WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(System.Net.WebSockets.WebSocketCloseStatus closeStatus, string? statusDescription, System.Threading.CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus closeStatus, string? statusDescription, System.Threading.CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<System.Net.WebSockets.WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, System.Threading.CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    [Fact]
    public async Task Settings_patch_broadcasts_to_users_other_connected_device()
    {
        // BUG B fix: a per-device settings change must propagate to the user's OTHER open windows.
        // The PATCH handler now fans the new settings out over the WS to every connected device of
        // the user except the one being edited. We register a capturing socket for "dev-other" in
        // the live registry, PATCH "dev-a", and assert dev-other received a {type:"settings"} frame.
        using var h = new Harness();
        var userId = await h.SignInAndUserIdAsync("oid-a", "alice@test", "Alice");
        var device = h.DeviceClient(await h.AddDeviceForUserAsync(userId));

        var registry = h.Factory.Services.GetRequiredService<ClipboardConnectionRegistry>();
        var otherSocket = new CapturingWebSocket();
        registry.Add(new ClipboardConnection { UserId = userId, DeviceId = "dev-other", Socket = otherSocket });

        var patch = await device.PatchAsJsonAsync("/api/clipboard/settings/dev-a", new
        {
            autoSync = false,
            send = true,
            receive = false,
            viewerHotkey = "Ctrl+Alt+V",
            density = "mini",
            showHints = false,
        });
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        otherSocket.Sent.Should().HaveCount(1);
        using var frame = JsonDocument.Parse(otherSocket.Sent[0]);
        frame.RootElement.GetProperty("type").GetString().Should().Be("settings");
        frame.RootElement.GetProperty("deviceId").GetString().Should().Be("dev-a");
        var s = frame.RootElement.GetProperty("settings");
        s.GetProperty("deviceId").GetString().Should().Be("dev-a");
        s.GetProperty("autoSync").GetBoolean().Should().BeFalse();
        s.GetProperty("receive").GetBoolean().Should().BeFalse();
        s.GetProperty("density").GetString().Should().Be("mini");
    }

    [Fact]
    public async Task Settings_patch_does_not_echo_to_the_origin_device()
    {
        // The window editing dev-a already applied the change locally, so the origin (dev-a) must NOT
        // receive its own settings frame back. Only OTHER devices do.
        using var h = new Harness();
        var userId = await h.SignInAndUserIdAsync("oid-a", "alice@test", "Alice");
        var device = h.DeviceClient(await h.AddDeviceForUserAsync(userId));

        var registry = h.Factory.Services.GetRequiredService<ClipboardConnectionRegistry>();
        var originSocket = new CapturingWebSocket();
        registry.Add(new ClipboardConnection { UserId = userId, DeviceId = "dev-a", Socket = originSocket });

        var patch = await device.PatchAsJsonAsync("/api/clipboard/settings/dev-a", new
        {
            autoSync = true,
            send = true,
            receive = true,
            viewerHotkey = "Ctrl+Win+Q",
            density = "rich",
            showHints = true,
        });
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        originSocket.Sent.Should().BeEmpty();
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
    public async Task Settings_patch_upserts_key_admission_fields_and_roundtrips()
    {
        // A device that cannot decrypt the E2E text key advertises its RSA public key and raises
        // needsTextKey through the regular settings PATCH; both GET shapes echo the fields back so
        // a key-holder can read them off the wire.
        using var h = new Harness();
        var userId = await h.SignInAndUserIdAsync("oid-a", "alice@test", "Alice");
        var device = h.DeviceClient(await h.AddDeviceForUserAsync(userId));

        var publicKey = B64("spki-public-key-bytes");
        var patch = await device.PatchAsJsonAsync("/api/clipboard/settings/dev-a", new
        {
            autoSync = true,
            send = true,
            receive = true,
            viewerHotkey = "Ctrl+Win+Q",
            density = "rich",
            showHints = true,
            publicKeyBase64 = publicKey,
            needsTextKey = true,
        });
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        var single = await device.GetFromJsonAsync<JsonElement>("/api/clipboard/settings/dev-a");
        single.GetProperty("publicKeyBase64").GetString().Should().Be(publicKey);
        single.GetProperty("needsTextKey").GetBoolean().Should().BeTrue();

        var list = await device.GetFromJsonAsync<JsonElement>("/api/clipboard/settings");
        list.GetArrayLength().Should().Be(1);
        list[0].GetProperty("publicKeyBase64").GetString().Should().Be(publicKey);
        list[0].GetProperty("needsTextKey").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Settings_patch_without_key_fields_keeps_stored_key_and_flag()
    {
        // The key-admission fields MERGE: a plain preferences save (older caller, no
        // publicKeyBase64/needsTextKey in the body) must not wipe the stored advertisement.
        using var h = new Harness();
        var userId = await h.SignInAndUserIdAsync("oid-a", "alice@test", "Alice");
        var device = h.DeviceClient(await h.AddDeviceForUserAsync(userId));

        var publicKey = B64("spki-public-key-bytes");
        (await device.PatchAsJsonAsync("/api/clipboard/settings/dev-a", new
        {
            autoSync = true,
            send = true,
            receive = true,
            viewerHotkey = "Ctrl+Win+Q",
            density = "rich",
            showHints = true,
            publicKeyBase64 = publicKey,
            needsTextKey = true,
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        // Second PATCH omits both fields entirely — only preferences change.
        (await device.PatchAsJsonAsync("/api/clipboard/settings/dev-a", new
        {
            autoSync = false,
            send = true,
            receive = true,
            viewerHotkey = "Ctrl+Alt+V",
            density = "mini",
            showHints = false,
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await device.GetFromJsonAsync<JsonElement>("/api/clipboard/settings/dev-a");
        dto.GetProperty("autoSync").GetBoolean().Should().BeFalse();
        dto.GetProperty("density").GetString().Should().Be("mini");
        dto.GetProperty("publicKeyBase64").GetString().Should().Be(publicKey);
        dto.GetProperty("needsTextKey").GetBoolean().Should().BeTrue();

        // And an explicit needsTextKey=false (admission done) clears the flag but keeps the key.
        (await device.PatchAsJsonAsync("/api/clipboard/settings/dev-a", new
        {
            autoSync = false,
            send = true,
            receive = true,
            viewerHotkey = "Ctrl+Alt+V",
            density = "mini",
            showHints = false,
            needsTextKey = false,
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var done = await device.GetFromJsonAsync<JsonElement>("/api/clipboard/settings/dev-a");
        done.GetProperty("needsTextKey").GetBoolean().Should().BeFalse();
        done.GetProperty("publicKeyBase64").GetString().Should().Be(publicKey);
    }

    [Fact]
    public async Task Settings_patch_invalid_base64_public_key_returns_400()
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
            density = "rich",
            showHints = true,
            publicKeyBase64 = "%%% not base64 %%%",
            needsTextKey = true,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Settings_patch_with_key_fields_broadcasts_them_to_other_devices()
    {
        // Holders must react LIVE to a new admission request: the settings frame fanned out on
        // PATCH carries needsTextKey + publicKeyBase64.
        using var h = new Harness();
        var userId = await h.SignInAndUserIdAsync("oid-a", "alice@test", "Alice");
        var device = h.DeviceClient(await h.AddDeviceForUserAsync(userId));

        var registry = h.Factory.Services.GetRequiredService<ClipboardConnectionRegistry>();
        var holderSocket = new CapturingWebSocket();
        registry.Add(new ClipboardConnection { UserId = userId, DeviceId = "dev-holder", Socket = holderSocket });

        var publicKey = B64("spki-public-key-bytes");
        (await device.PatchAsJsonAsync("/api/clipboard/settings/dev-new", new
        {
            autoSync = true,
            send = true,
            receive = true,
            viewerHotkey = "Ctrl+Win+Q",
            density = "rich",
            showHints = true,
            publicKeyBase64 = publicKey,
            needsTextKey = true,
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        holderSocket.Sent.Should().HaveCount(1);
        using var frame = JsonDocument.Parse(holderSocket.Sent[0]);
        frame.RootElement.GetProperty("type").GetString().Should().Be("settings");
        frame.RootElement.GetProperty("deviceId").GetString().Should().Be("dev-new");
        var s = frame.RootElement.GetProperty("settings");
        s.GetProperty("needsTextKey").GetBoolean().Should().BeTrue();
        s.GetProperty("publicKeyBase64").GetString().Should().Be(publicKey);
    }

    [Fact]
    public async Task Clipboard_devices_lists_roster_with_online_and_key_admission_fields()
    {
        using var h = new Harness();
        var userId = await h.SignInAndUserIdAsync("oid-a", "alice@test", "Alice");

        // Two devices with KNOWN ids so the list rows can be matched deterministically.
        var devices = h.Factory.Services.GetRequiredService<IDeviceStore>();
        var keyA = ApiKeyGenerator.Generate();
        await devices.AddAsync(new Device
        {
            Id = "dev-a",
            UserId = userId,
            Name = "Laptop",
            ApiKeyHash = ApiKeyHasher.Hash(keyA),
            CreatedUtc = DateTimeOffset.UtcNow,
        });
        await devices.AddAsync(new Device
        {
            Id = "dev-b",
            UserId = userId,
            Name = "Desktop",
            ApiKeyHash = ApiKeyHasher.Hash(ApiKeyGenerator.Generate()),
            CreatedUtc = DateTimeOffset.UtcNow,
        });
        var device = h.DeviceClient(keyA);

        // dev-b advertises it needs the text key; dev-a never stored a settings row at all.
        var publicKey = B64("dev-b-public-key");
        (await device.PatchAsJsonAsync("/api/clipboard/settings/dev-b", new
        {
            autoSync = true,
            send = true,
            receive = true,
            viewerHotkey = "Ctrl+Win+Q",
            density = "rich",
            showHints = true,
            publicKeyBase64 = publicKey,
            needsTextKey = true,
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        // Only dev-b is connected over the WS.
        var registry = h.Factory.Services.GetRequiredService<ClipboardConnectionRegistry>();
        registry.Add(new ClipboardConnection { UserId = userId, DeviceId = "dev-b", Socket = new CapturingWebSocket() });

        var list = await device.GetFromJsonAsync<JsonElement>("/api/clipboard/devices");
        list.GetArrayLength().Should().Be(2);

        JsonElement Row(string id)
        {
            foreach (var row in list.EnumerateArray())
                if (row.GetProperty("deviceId").GetString() == id) return row;
            throw new InvalidOperationException($"device {id} not in list");
        }

        var a = Row("dev-a");
        a.GetProperty("name").GetString().Should().Be("Laptop");
        a.GetProperty("online").GetBoolean().Should().BeFalse();
        a.GetProperty("needsTextKey").GetBoolean().Should().BeFalse();
        a.GetProperty("publicKeyBase64").ValueKind.Should().Be(JsonValueKind.Null);

        var b = Row("dev-b");
        b.GetProperty("name").GetString().Should().Be("Desktop");
        b.GetProperty("online").GetBoolean().Should().BeTrue();
        b.GetProperty("needsTextKey").GetBoolean().Should().BeTrue();
        b.GetProperty("publicKeyBase64").GetString().Should().Be(publicKey);
    }

    [Fact]
    public async Task Clipboard_devices_is_user_scoped()
    {
        using var h = new Harness();

        // User B owns a device that is waiting for the text key.
        var bUserId = await h.SignInAndUserIdAsync("oid-b", "bob@test", "Bob");
        var devices = h.Factory.Services.GetRequiredService<IDeviceStore>();
        var bKey = ApiKeyGenerator.Generate();
        await devices.AddAsync(new Device
        {
            Id = "dev-b",
            UserId = bUserId,
            Name = "Bobs PC",
            ApiKeyHash = ApiKeyHasher.Hash(bKey),
            CreatedUtc = DateTimeOffset.UtcNow,
        });
        var bDevice = h.DeviceClient(bKey);
        (await bDevice.PatchAsJsonAsync("/api/clipboard/settings/dev-b", new
        {
            autoSync = true,
            send = true,
            receive = true,
            viewerHotkey = "Ctrl+Win+Q",
            density = "rich",
            showHints = true,
            publicKeyBase64 = B64("bob-public-key"),
            needsTextKey = true,
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        // User A's device list contains only A's device — B's roster, key and flag never leak.
        var aUserId = await h.SignInAndUserIdAsync("oid-a", "alice@test", "Alice");
        var aDevice = h.DeviceClient(await h.AddDeviceForUserAsync(aUserId, name: "Alices PC"));
        var aList = await aDevice.GetFromJsonAsync<JsonElement>("/api/clipboard/devices");
        aList.GetArrayLength().Should().Be(1);
        aList[0].GetProperty("name").GetString().Should().Be("Alices PC");
        aList[0].GetProperty("needsTextKey").GetBoolean().Should().BeFalse();

        // B still sees its own advertised state.
        var bList = await bDevice.GetFromJsonAsync<JsonElement>("/api/clipboard/devices");
        bList.GetArrayLength().Should().Be(1);
        bList[0].GetProperty("deviceId").GetString().Should().Be("dev-b");
        bList[0].GetProperty("needsTextKey").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Clipboard_devices_without_auth_returns_401()
    {
        using var h = new Harness();
        var resp = await h.Anonymous().GetAsync("/api/clipboard/devices");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
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
    public async Task Publish_duplicate_of_newest_item_returns_existing_id_and_does_not_append_or_broadcast()
    {
        // Defense-in-depth against client echo loops (RDP clipboard redirection re-announces a set
        // and the OS multi-fires capture events): a publish byte-identical to the user's newest
        // history item is acknowledged with the EXISTING id, appends nothing and broadcasts nothing.
        using var h = new Harness();
        var userId = await h.SignInAndUserIdAsync("oid-a", "alice@test", "Alice");
        var device = h.DeviceClient(await h.AddDeviceForUserAsync(userId));

        var payload = B64("looping content");
        (await device.PostAsJsonAsync("/api/clipboard/items",
            PublishBody("item-1", "Text", "dev-a", payload)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Register the peer socket AFTER the first publish so only the duplicate could reach it.
        var registry = h.Factory.Services.GetRequiredService<ClipboardConnectionRegistry>();
        var peerSocket = new CapturingWebSocket();
        registry.Add(new ClipboardConnection { UserId = userId, DeviceId = "dev-peer", Socket = peerSocket });

        var dup = await device.PostAsJsonAsync("/api/clipboard/items",
            PublishBody("item-2", "Text", "dev-a", payload));
        dup.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = JsonDocument.Parse(await dup.Content.ReadAsStringAsync()))
            doc.RootElement.GetProperty("id").GetString().Should().Be("item-1"); // the EXISTING row

        peerSocket.Sent.Should().BeEmpty(); // never re-broadcast back into the loop

        var history = await device.GetFromJsonAsync<JsonElement>("/api/clipboard/history");
        history.GetArrayLength().Should().Be(1);
        history[0].GetProperty("id").GetString().Should().Be("item-1");
    }

    [Fact]
    public async Task Publish_same_content_as_an_older_non_head_item_still_appends()
    {
        // Only the HEAD is deduped: re-copying content that was published earlier (but is no longer
        // the newest item) is a legitimate new clipboard event and must append normally.
        using var h = new Harness();
        var userId = await h.SignInAndUserIdAsync("oid-a", "alice@test", "Alice");
        var device = h.DeviceClient(await h.AddDeviceForUserAsync(userId));

        (await device.PostAsJsonAsync("/api/clipboard/items",
            PublishBody("item-1", "Text", "dev-a", B64("first"))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await device.PostAsJsonAsync("/api/clipboard/items",
            PublishBody("item-2", "Text", "dev-a", B64("second"))))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var again = await device.PostAsJsonAsync("/api/clipboard/items",
            PublishBody("item-3", "Text", "dev-a", B64("first")));
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = JsonDocument.Parse(await again.Content.ReadAsStringAsync()))
            doc.RootElement.GetProperty("id").GetString().Should().Be("item-3");

        var history = await device.GetFromJsonAsync<JsonElement>("/api/clipboard/history");
        history.GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task Publish_same_payload_different_type_is_not_deduped()
    {
        // The head dedupe matches type + payload; identical bytes under a different type are a
        // distinct item.
        using var h = new Harness();
        var userId = await h.SignInAndUserIdAsync("oid-a", "alice@test", "Alice");
        var device = h.DeviceClient(await h.AddDeviceForUserAsync(userId));

        var payload = B64("same-bytes-either-way");
        (await device.PostAsJsonAsync("/api/clipboard/items",
            PublishBody("item-text", "Text", "dev-a", payload)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await device.PostAsJsonAsync("/api/clipboard/items",
            PublishBody("item-img", "Image", "dev-a", payload, sizeBytes: 21)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var history = await device.GetFromJsonAsync<JsonElement>("/api/clipboard/history");
        history.GetArrayLength().Should().Be(2);
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
    public async Task Delete_removes_the_item_from_history()
    {
        using var h = new Harness();
        var userId = await h.SignInAndUserIdAsync("oid-a", "alice@test", "Alice");
        var device = h.DeviceClient(await h.AddDeviceForUserAsync(userId));

        (await device.PostAsJsonAsync("/api/clipboard/items",
            PublishBody("item-1", "Text", "dev-a", B64("hello"))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await device.PostAsJsonAsync("/api/clipboard/items",
            PublishBody("item-2", "Text", "dev-a", B64("world"))))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var del = await device.DeleteAsync("/api/clipboard/items/item-1");
        del.StatusCode.Should().Be(HttpStatusCode.OK);

        var history = await device.GetFromJsonAsync<JsonElement>("/api/clipboard/history");
        history.GetArrayLength().Should().Be(1);
        history[0].GetProperty("id").GetString().Should().Be("item-2");
    }

    [Fact]
    public async Task Delete_without_auth_returns_401()
    {
        using var h = new Harness();
        var resp = await h.Anonymous().DeleteAsync("/api/clipboard/items/whatever");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Delete_is_user_scoped_and_cannot_remove_another_users_item()
    {
        using var h = new Harness();

        // User B owns item-b.
        var bUserId = await h.SignInAndUserIdAsync("oid-b", "bob@test", "Bob");
        var bDevice = h.DeviceClient(await h.AddDeviceForUserAsync(bUserId));
        (await bDevice.PostAsJsonAsync("/api/clipboard/items",
            PublishBody("item-b", "Text", "dev-b", B64("bob secret"))))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // User A deletes by the same id: the store is user-scoped, so it is a no-op for B's row.
        var aUserId = await h.SignInAndUserIdAsync("oid-a", "alice@test", "Alice");
        var aDevice = h.DeviceClient(await h.AddDeviceForUserAsync(aUserId));
        (await aDevice.DeleteAsync("/api/clipboard/items/item-b"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // B still has its item.
        var bHistory = await bDevice.GetFromJsonAsync<JsonElement>("/api/clipboard/history");
        bHistory.GetArrayLength().Should().Be(1);
        bHistory[0].GetProperty("id").GetString().Should().Be("item-b");
    }

    [Fact]
    public async Task Delete_broadcasts_to_users_other_connected_device_not_origin()
    {
        // Deleting an item fans a {type:"deleted", id} frame out to the user's OTHER connected devices
        // so an open clipboard screen / floating viewer drops the row live. The origin (the api-key
        // principal's device) is excluded — the caller already removed it locally. We register a
        // capturing socket for "dev-other" plus one for the deleting device, delete, and assert only
        // dev-other received the frame.
        using var h = new Harness();
        var userId = await h.SignInAndUserIdAsync("oid-a", "alice@test", "Alice");

        // Bind the device under a known device id so we can register its socket as the origin.
        var devices = h.Factory.Services.GetRequiredService<IDeviceStore>();
        var key = ApiKeyGenerator.Generate();
        await devices.AddAsync(new Device
        {
            Id = "dev-a",
            UserId = userId,
            Name = "Laptop",
            ApiKeyHash = ApiKeyHasher.Hash(key),
            CreatedUtc = DateTimeOffset.UtcNow,
        });
        var device = h.DeviceClient(key);

        (await device.PostAsJsonAsync("/api/clipboard/items",
            PublishBody("item-1", "Text", "dev-a", B64("hello"))))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var registry = h.Factory.Services.GetRequiredService<ClipboardConnectionRegistry>();
        var originSocket = new CapturingWebSocket();
        var otherSocket = new CapturingWebSocket();
        registry.Add(new ClipboardConnection { UserId = userId, DeviceId = "dev-a", Socket = originSocket });
        registry.Add(new ClipboardConnection { UserId = userId, DeviceId = "dev-other", Socket = otherSocket });

        var del = await device.DeleteAsync("/api/clipboard/items/item-1");
        del.StatusCode.Should().Be(HttpStatusCode.OK);

        originSocket.Sent.Should().BeEmpty();
        otherSocket.Sent.Should().HaveCount(1);
        using var frame = JsonDocument.Parse(otherSocket.Sent[0]);
        frame.RootElement.GetProperty("type").GetString().Should().Be("deleted");
        frame.RootElement.GetProperty("id").GetString().Should().Be("item-1");
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

    [Fact]
    public async Task Retention_get_defaults_to_null()
    {
        using var h = new Harness();
        await h.SignInAndUserIdAsync("oid-r1", "r1@test", "R1");
        var client = await CookieAuthHelper.SignInAsync(h.Factory);

        var resp = await client.GetAsync("/api/clipboard/retention");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("hours", out var hours).Should().BeTrue();
        hours.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Retention_put_then_get_roundtrips()
    {
        using var h = new Harness();
        await h.SignInAndUserIdAsync("oid-r2", "r2@test", "R2");
        var client = await CookieAuthHelper.SignInAsync(h.Factory);

        var put = await client.PutAsJsonAsync("/api/clipboard/retention", new { hours = 6 });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var get = await client.GetAsync("/api/clipboard/retention");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await get.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("hours").GetInt32().Should().Be(6);
    }

    [Fact]
    public async Task Retention_put_out_of_range_returns_400()
    {
        using var h = new Harness();
        await h.SignInAndUserIdAsync("oid-r3", "r3@test", "R3");
        var client = await CookieAuthHelper.SignInAsync(h.Factory);

        var low = await client.PutAsJsonAsync("/api/clipboard/retention", new { hours = 0 });
        low.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var high = await client.PutAsJsonAsync("/api/clipboard/retention", new { hours = 1000 });
        high.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
