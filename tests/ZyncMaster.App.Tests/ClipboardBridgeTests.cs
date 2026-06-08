using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using ZyncMaster.App.Bridge;
using ZyncMaster.App.Configuration;
using ZyncMaster.App.State;
using ZyncMaster.Core;
using ZyncMaster.Engine;
using Xunit;

namespace ZyncMaster.App.Tests;

// Plan 2 Task 10 — the clipboard bridge actions. Two layers:
//   * UiBridge dispatch: each clipboard action round-trips camelCase JSON and calls the right
//     IEngineActions method (a thin fake captures the call).
//   * EngineActions behaviour over fakes (transport / sink / keystore / devices): history DECRYPTS
//     text (never leaks ciphertext), updateClipboardSettings validates density, pasteClipboardEntry
//     on an unknown id is a clean no-op, getClipboardDevices merges roster + per-device settings +
//     online + isThis, and setClipboardHotkey re-registers + persists.
public class ClipboardBridgeTests
{
    // ---------- UiBridge dispatch (camelCase round-trip + routing) ----------

    private sealed class FakeTransport : IBridgeTransport
    {
        public List<string> Sent { get; } = new();
        public event Action<string>? Received;
        public void Send(string json) => Sent.Add(json);
        public void PushInbound(string json) => Received?.Invoke(json);
    }

    // Minimal IEngineActions that captures only the clipboard calls; every other member throws so a
    // stray dispatch is obvious. NotImplementedException is fine — these tests only push clipboard
    // actions.
    private sealed class ClipboardSpyActions : IEngineActions
    {
        public int GetHistoryCalls;
        public int GetDevicesCalls;
        public string? UpdateSettingsArg;
        public string? PasteArg;
        public bool PasteToReturn = true;
        public string? HotkeyArg;
        public int CloseViewerCalls;

        public IReadOnlyList<ClipboardHistoryItem> HistoryToReturn = new List<ClipboardHistoryItem>
        {
            new() { Id = "i1", Type = "Text", Text = "hello", OriginDeviceId = "dev-1", CreatedUtc = DateTimeOffset.UnixEpoch },
        };
        public ClipboardDevicesView DevicesToReturn = new()
        {
            ThisDeviceId = "dev-1",
            Devices = new List<ClipboardDeviceView>
            {
                new() { Id = "dev-1", Name = "Studio PC", Online = true, IsThis = true,
                        Settings = new ClipboardSettingsView { Density = "rich", ViewerHotkey = "Ctrl+Win+Q" } },
            },
        };

        public Task<IReadOnlyList<ClipboardHistoryItem>> GetClipboardHistoryAsync(CancellationToken ct = default)
        { GetHistoryCalls++; return Task.FromResult(HistoryToReturn); }
        public Task<ClipboardDevicesView> GetClipboardDevicesAsync(CancellationToken ct = default)
        { GetDevicesCalls++; return Task.FromResult(DevicesToReturn); }
        public Task UpdateClipboardSettingsAsync(string payloadJson, CancellationToken ct = default)
        { UpdateSettingsArg = payloadJson; return Task.CompletedTask; }
        public Task<bool> PasteClipboardEntryAsync(string id, CancellationToken ct = default)
        { PasteArg = id; return Task.FromResult(PasteToReturn); }
        public Task SetClipboardHotkeyAsync(string hotkey, CancellationToken ct = default)
        { HotkeyArg = hotkey; return Task.CompletedTask; }
        public Task CloseClipboardViewerAsync(CancellationToken ct = default)
        { CloseViewerCalls++; return Task.CompletedTask; }

        // ---- everything else is out of scope for these tests ----
        public Task<ServerHealth> CheckServerHealthAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<AppStatus> GetStatusAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SyncResult> SyncNowAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<PairingOutcome> PairAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task SaveConfigAsync(string configJson, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetPausedAsync(bool paused, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<AccountInfo>> ListAccountsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<CalendarInfo>> ListCalendarsAsync(string accountRef, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> ListLocalCalendarsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CalendarInfo> CreateCalendarAsync(string accountRef, string name, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SyncPair> CreatePairAsync(string requestJson, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<SyncPair>> ListPairsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<SyncPair> UpdatePairAsync(string requestJson, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeletePairAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<CleanupResult> CleanupOldDestinationAsync(string pairId, Endpoint oldDestination, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CountManagedInDestinationAsync(string pairId, Endpoint oldDestination, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<MirrorResult> RunPairNowAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<RequestSyncResult> RequestPairSyncAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> UnlinkAccountAsync(string accountRef, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string?> EnsureDeviceRegisteredAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DeviceInfo> GetDeviceAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DeviceInfo> RenameDeviceAsync(string name, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> CheckDeviceNameAsync(string name, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string?> GenerateTxtAsync(string requestJson, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string?> ExportSourceTxtAsync(string requestJson, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<AppCapabilities> GetCapabilitiesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> GetAutoStartAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetAutoStartAsync(bool enabled, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IdentityState> GetIdentityStateAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<LoginOutcome> LoginAsync(string provider, string? email, CancellationToken ct = default) => throw new NotImplementedException();
        public Task CancelLoginAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task SignOutAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ConnectCalendarOutcome> ConnectCalendarAsync(string scope, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ConnectCalendarOutcome> UpgradeAccountScopeAsync(string accountId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<CalendarAccountSummary>> ListCalendarAccountsAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task OpenLicensesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task CancelConnectAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private static string Message(string action, string? correlationId, string? payload = null)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["action"] = action,
            ["correlationId"] = correlationId,
            ["payload"] = payload,
        });

    private static JsonElement LastReply(FakeTransport transport)
    {
        transport.Sent.Should().NotBeEmpty();
        return JsonSerializer.Deserialize<JsonElement>(transport.Sent[^1]);
    }

    [Fact]
    public void GetClipboardHistory_calls_engine_and_replies_with_decrypted_text_array()
    {
        var transport = new FakeTransport();
        var engine = new ClipboardSpyActions();
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("getClipboardHistory", "h1"));

        engine.GetHistoryCalls.Should().Be(1);
        var reply = LastReply(transport);
        reply.GetProperty("correlationId").GetString().Should().Be("h1");
        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
        var payload = JsonSerializer.Deserialize<JsonElement>(reply.GetProperty("payload").GetString()!);
        payload.GetArrayLength().Should().Be(1);
        payload[0].GetProperty("id").GetString().Should().Be("i1");
        payload[0].GetProperty("type").GetString().Should().Be("Text");
        payload[0].GetProperty("text").GetString().Should().Be("hello");
        // camelCase wire names.
        payload[0].GetProperty("originDeviceId").GetString().Should().Be("dev-1");
    }

    [Fact]
    public void GetClipboardDevices_calls_engine_and_replies_with_thisDeviceId_and_devices()
    {
        var transport = new FakeTransport();
        var engine = new ClipboardSpyActions();
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("getClipboardDevices", "d1"));

        engine.GetDevicesCalls.Should().Be(1);
        var reply = LastReply(transport);
        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
        var payload = JsonSerializer.Deserialize<JsonElement>(reply.GetProperty("payload").GetString()!);
        payload.GetProperty("thisDeviceId").GetString().Should().Be("dev-1");
        var dev = payload.GetProperty("devices")[0];
        dev.GetProperty("id").GetString().Should().Be("dev-1");
        dev.GetProperty("online").GetBoolean().Should().BeTrue();
        dev.GetProperty("isThis").GetBoolean().Should().BeTrue();
        dev.GetProperty("settings").GetProperty("density").GetString().Should().Be("rich");
    }

    [Fact]
    public void UpdateClipboardSettings_passes_payload_and_replies_ok()
    {
        var transport = new FakeTransport();
        var engine = new ClipboardSpyActions();
        _ = new UiBridge(transport, engine);

        var json = "{\"deviceId\":\"dev-1\",\"density\":\"mini\",\"autoSync\":false}";
        transport.PushInbound(Message("updateClipboardSettings", "u1", json));

        engine.UpdateSettingsArg.Should().Be(json);
        var reply = LastReply(transport);
        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
        var payload = JsonSerializer.Deserialize<JsonElement>(reply.GetProperty("payload").GetString()!);
        payload.GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void PasteClipboardEntry_passes_id_and_replies_status_ok()
    {
        var transport = new FakeTransport();
        var engine = new ClipboardSpyActions { PasteToReturn = true };
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("pasteClipboardEntry", "p1", "i1"));

        engine.PasteArg.Should().Be("i1");
        var payload = JsonSerializer.Deserialize<JsonElement>(LastReply(transport).GetProperty("payload").GetString()!);
        payload.GetProperty("status").GetString().Should().Be("ok");
    }

    [Fact]
    public void PasteClipboardEntry_unknown_id_replies_status_notfound()
    {
        var transport = new FakeTransport();
        var engine = new ClipboardSpyActions { PasteToReturn = false };
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("pasteClipboardEntry", "p2", "missing"));

        engine.PasteArg.Should().Be("missing");
        var payload = JsonSerializer.Deserialize<JsonElement>(LastReply(transport).GetProperty("payload").GetString()!);
        payload.GetProperty("status").GetString().Should().Be("notfound");
    }

    [Fact]
    public void SetClipboardHotkey_passes_hotkey_and_replies_ok()
    {
        var transport = new FakeTransport();
        var engine = new ClipboardSpyActions();
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("setClipboardHotkey", "k1", "Ctrl+Shift+V"));

        engine.HotkeyArg.Should().Be("Ctrl+Shift+V");
        LastReply(transport).GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void CloseClipboardViewer_calls_engine()
    {
        var transport = new FakeTransport();
        var engine = new ClipboardSpyActions();
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("closeClipboardViewer", "c1"));

        engine.CloseViewerCalls.Should().Be(1);
        LastReply(transport).GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void PushClipboardItem_sends_a_clipboard_item_event()
    {
        var transport = new FakeTransport();
        var engine = new ClipboardSpyActions();
        var bridge = new UiBridge(transport, engine);

        bridge.PushClipboardItem(new ClipboardHistoryItem { Id = "x1", Type = "Text", Text = "live" });

        transport.Sent.Should().ContainSingle();
        var msg = JsonSerializer.Deserialize<JsonElement>(transport.Sent[0]);
        msg.GetProperty("event").GetString().Should().Be("clipboard:item");
        msg.GetProperty("payload").GetProperty("id").GetString().Should().Be("x1");
        msg.GetProperty("payload").GetProperty("text").GetString().Should().Be("live");
    }

    // ---------- EngineActions behaviour over fakes ----------

    private static EngineActions BuildEngine(
        Mock<IClipboardTransport>? transport = null,
        Mock<IClipboardSink>? sink = null,
        Mock<IClipboardKeyStore>? keys = null,
        Mock<IClipboardDevicesSource>? devices = null,
        Mock<IClipboardHotkey>? hotkey = null,
        IDeviceKeyStore? deviceKeyStore = null,
        IClock? clock = null)
    {
        var settings = new EngineSettings { ServerBaseUrl = "https://server.test" };
        var deviceKeys = deviceKeyStore ?? KeyStore("device-key").Object;

        var pairing = new PairingService(
            new Mock<IPairingClient>().Object, new Mock<IBrowserLauncher>().Object, deviceKeys, settings);
        var sync = new SyncEngine(
            deviceKeys, new Mock<ICalendarSource>().Object, new Mock<ISyncClient>().Object,
            new Mock<IClock>().Object, settings);
        var identity = new IdentityLoginService(
            new Mock<IIdentityServerClient>().Object, new Mock<IIdentityTokenCache>().Object,
            () => new Mock<IIdentityLoopback>().Object, new Mock<ISystemBrowser>().Object, "https://server.test");
        var calendarConnect = new CalendarConnectService(
            new Mock<ICalendarServerClient>().Object, new Mock<IIdentityTokenCache>().Object,
            () => new Mock<IIdentityLoopback>().Object, new Mock<ISystemBrowser>().Object);

        return new EngineActions(
            deviceKeys, pairing, sync,
            new Mock<ISettingsRepository<AppSettings>>().Object, new AppSettingsResolver(), "settings.json",
            new Mock<IPairsClient>().Object, new Mock<IIdentityTokenCache>().Object,
            new BasicTxtExporter(new Mock<ICalExportRunner>().Object), new Mock<IAutoStartManager>().Object,
            settings, _ => Task.FromResult<string?>(null), "host.exe",
            identity, calendarConnect,
            new Mock<IOutlookComProbe>().Object, new Mock<ICalendarSource>().Object,
            new Mock<ICalExportRunner>().Object, clock ?? new Mock<IClock>().Object,
            new HttpClient(), NullAppLogger.Instance,
            ownedHttp: null,
            clipboardTransport: (transport ?? new Mock<IClipboardTransport>()).Object,
            clipboardSink: (sink ?? new Mock<IClipboardSink>()).Object,
            clipboardKeys: (keys ?? new Mock<IClipboardKeyStore>()).Object,
            clipboardHotkey: (hotkey ?? new Mock<IClipboardHotkey>()).Object,
            clipboardDevices: (devices ?? new Mock<IClipboardDevicesSource>()).Object);
    }

    private static Mock<IDeviceKeyStore> KeyStore(string? key)
    {
        var keys = new Mock<IDeviceKeyStore>();
        keys.Setup(k => k.LoadAsync(It.IsAny<CancellationToken>())).ReturnsAsync(key);
        return keys;
    }

    [Fact]
    public async Task GetHistory_decrypts_text_and_never_returns_ciphertext()
    {
        var key = TextCrypto.NewKey();
        var cipher = TextCrypto.Encrypt(key, "secret message");

        var transport = new Mock<IClipboardTransport>();
        transport.Setup(t => t.GetHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ClipboardEntry>)new[]
            {
                new ClipboardEntry { Id = "i1", Type = ClipboardEntryType.Text, CipherText = cipher, OriginDeviceId = "dev-1" },
            });

        var keys = new Mock<IClipboardKeyStore>();
        keys.Setup(k => k.LoadTextKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(key);

        var actions = BuildEngine(transport, keys: keys);

        var history = await actions.GetClipboardHistoryAsync(CancellationToken.None);

        history.Should().ContainSingle();
        history[0].Text.Should().Be("secret message");
        history[0].Type.Should().Be("Text");
    }

    [Fact]
    public async Task GetHistory_without_key_returns_text_null_no_leak()
    {
        var key = TextCrypto.NewKey();
        var cipher = TextCrypto.Encrypt(key, "secret message");

        var transport = new Mock<IClipboardTransport>();
        transport.Setup(t => t.GetHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ClipboardEntry>)new[]
            {
                new ClipboardEntry { Id = "i1", Type = ClipboardEntryType.Text, CipherText = cipher, OriginDeviceId = "dev-1" },
            });

        var keys = new Mock<IClipboardKeyStore>();
        keys.Setup(k => k.LoadTextKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync((byte[]?)null);

        var actions = BuildEngine(transport, keys: keys);

        var history = await actions.GetClipboardHistoryAsync(CancellationToken.None);

        history[0].Text.Should().BeNull();
    }

    [Fact]
    public async Task GetHistory_image_surfaces_thumbnail_as_png_data_uri()
    {
        var thumb = new byte[] { 1, 2, 3, 4 };
        var transport = new Mock<IClipboardTransport>();
        transport.Setup(t => t.GetHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ClipboardEntry>)new[]
            {
                new ClipboardEntry { Id = "img1", Type = ClipboardEntryType.Image, Thumbnail = thumb, SizeBytes = 99, OriginDeviceId = "dev-1" },
            });

        var actions = BuildEngine(transport);

        var history = await actions.GetClipboardHistoryAsync(CancellationToken.None);

        history[0].Text.Should().BeNull();
        history[0].ImagePreviewDataUri.Should().Be("data:image/png;base64," + Convert.ToBase64String(thumb));
        history[0].SizeBytes.Should().Be(99);
    }

    [Fact]
    public async Task GetClipboardDevices_merges_roster_settings_online_and_isThis()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new Mock<IClock>();
        clock.SetupGet(c => c.UtcNow).Returns(now);

        var devices = new Mock<IClipboardDevicesSource>();
        devices.Setup(d => d.ListDevicesAsync("device-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ClipboardDeviceRow>)new[]
            {
                new ClipboardDeviceRow { Id = "dev-1", Name = "Studio PC", LastSeenUtc = now.AddMinutes(-1) }, // online
                new ClipboardDeviceRow { Id = "dev-2", Name = "Laptop", LastSeenUtc = now.AddHours(-2) },      // offline
            });

        var transport = new Mock<IClipboardTransport>();
        transport.Setup(t => t.GetSettingsAsync("dev-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClipboardSettings { Density = "mini" });
        transport.Setup(t => t.GetSettingsAsync("dev-2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClipboardSettings { Density = "rich" });

        var actions = BuildEngine(transport, devices: devices, clock: clock.Object);
        actions.InitializeClipboard(new ClipboardSettings(), "dev-1", "Studio PC");

        var view = await actions.GetClipboardDevicesAsync(CancellationToken.None);

        view.ThisDeviceId.Should().Be("dev-1");
        view.Devices.Should().HaveCount(2);

        var d1 = view.Devices[0];
        d1.Id.Should().Be("dev-1");
        d1.Online.Should().BeTrue();
        d1.IsThis.Should().BeTrue();
        d1.Settings.Density.Should().Be("mini");

        var d2 = view.Devices[1];
        d2.Online.Should().BeFalse();   // last seen 2h ago is outside the online window
        d2.IsThis.Should().BeFalse();
        d2.Settings.Density.Should().Be("rich");
    }

    [Fact]
    public async Task GetClipboardDevices_prefers_live_presence_over_lastSeen()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new Mock<IClock>();
        clock.SetupGet(c => c.UtcNow).Returns(now);

        var devices = new Mock<IClipboardDevicesSource>();
        devices.Setup(d => d.ListDevicesAsync("device-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ClipboardDeviceRow>)new[]
            {
                // dev-1 was last seen long ago (heuristic => offline) but presence says it IS online.
                new ClipboardDeviceRow { Id = "dev-1", Name = "Studio PC", LastSeenUtc = now.AddHours(-3) },
                // dev-2 was last seen recently (heuristic => online) but presence does NOT list it.
                new ClipboardDeviceRow { Id = "dev-2", Name = "Laptop", LastSeenUtc = now.AddMinutes(-1) },
            });

        var transport = new Mock<IClipboardTransport>();
        transport.Setup(t => t.GetSettingsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClipboardSettings());

        var actions = BuildEngine(transport, devices: devices, clock: clock.Object);

        // The server pushes a presence frame: only dev-1 is online.
        transport.Raise(t => t.PresenceChanged += null,
            (IReadOnlyList<string>)new[] { "dev-1" });

        var view = await actions.GetClipboardDevicesAsync(CancellationToken.None);

        // Presence is authoritative: dev-1 online (despite stale last-seen), dev-2 offline (despite
        // fresh last-seen).
        view.Devices.Should().HaveCount(2);
        view.Devices[0].Id.Should().Be("dev-1");
        view.Devices[0].Online.Should().BeTrue();
        view.Devices[1].Id.Should().Be("dev-2");
        view.Devices[1].Online.Should().BeFalse();
    }

    [Fact]
    public async Task GetClipboardDevices_falls_back_to_lastSeen_when_no_presence_received()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new Mock<IClock>();
        clock.SetupGet(c => c.UtcNow).Returns(now);

        var devices = new Mock<IClipboardDevicesSource>();
        devices.Setup(d => d.ListDevicesAsync("device-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ClipboardDeviceRow>)new[]
            {
                new ClipboardDeviceRow { Id = "dev-1", Name = "Studio PC", LastSeenUtc = now.AddMinutes(-1) }, // online
                new ClipboardDeviceRow { Id = "dev-2", Name = "Laptop", LastSeenUtc = now.AddHours(-2) },      // offline
            });

        var transport = new Mock<IClipboardTransport>();
        transport.Setup(t => t.GetSettingsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClipboardSettings());

        var actions = BuildEngine(transport, devices: devices, clock: clock.Object);

        // No PresenceChanged ever fires -> the last-seen heuristic governs.
        var view = await actions.GetClipboardDevicesAsync(CancellationToken.None);

        view.Devices[0].Online.Should().BeTrue();
        view.Devices[1].Online.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateClipboardSettings_persists_via_transport_and_validates_density()
    {
        var transport = new Mock<IClipboardTransport>();
        var actions = BuildEngine(transport);

        await actions.UpdateClipboardSettingsAsync(
            "{\"deviceId\":\"dev-1\",\"autoSync\":true,\"send\":false,\"receive\":true,\"viewerHotkey\":\"Ctrl+Win+Q\",\"density\":\"mini\",\"showHints\":false}",
            CancellationToken.None);

        transport.Verify(t => t.UpdateSettingsAsync(
            "dev-1",
            It.Is<ClipboardSettings>(s => s.Density == "mini" && s.Send == false && s.ShowHints == false),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("loud")]
    [InlineData("")]
    [InlineData("RICH")] // case-sensitive: only lower-case "rich"/"mini" are valid
    public async Task UpdateClipboardSettings_rejects_invalid_density(string density)
    {
        var transport = new Mock<IClipboardTransport>();
        var actions = BuildEngine(transport);

        Func<Task> act = () => actions.UpdateClipboardSettingsAsync(
            $"{{\"deviceId\":\"dev-1\",\"density\":\"{density}\"}}", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        transport.Verify(t => t.UpdateSettingsAsync(It.IsAny<string>(), It.IsAny<ClipboardSettings>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UpdateClipboardSettings_missing_deviceId_throws()
    {
        var transport = new Mock<IClipboardTransport>();
        var actions = BuildEngine(transport);

        Func<Task> act = () => actions.UpdateClipboardSettingsAsync("{\"density\":\"rich\"}", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task UpdateClipboardSettings_for_this_device_updates_live_settings()
    {
        var transport = new Mock<IClipboardTransport>();
        var actions = BuildEngine(transport);
        actions.InitializeClipboard(new ClipboardSettings { Density = "rich" }, "dev-1", "Studio PC");

        await actions.UpdateClipboardSettingsAsync(
            "{\"deviceId\":\"dev-1\",\"density\":\"mini\",\"autoSync\":false}", CancellationToken.None);

        actions.CurrentClipboardSettings.Density.Should().Be("mini");
        actions.CurrentClipboardSettings.AutoSync.Should().BeFalse();
    }

    [Fact]
    public async Task PasteClipboardEntry_unknown_id_is_clean_no_op()
    {
        var transport = new Mock<IClipboardTransport>();
        transport.Setup(t => t.GetHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ClipboardEntry>)Array.Empty<ClipboardEntry>());
        var sink = new Mock<IClipboardSink>();

        var actions = BuildEngine(transport, sink: sink);

        var found = await actions.PasteClipboardEntryAsync("nope", CancellationToken.None);

        found.Should().BeFalse();
        sink.Verify(s => s.PasteIntoFocusedAsync(It.IsAny<ClipboardEntry>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PasteClipboardEntry_known_text_decrypts_then_pastes_and_closes_viewer()
    {
        var key = TextCrypto.NewKey();
        var cipher = TextCrypto.Encrypt(key, "paste me");

        var transport = new Mock<IClipboardTransport>();
        transport.Setup(t => t.GetHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ClipboardEntry>)new[]
            {
                new ClipboardEntry { Id = "i1", Type = ClipboardEntryType.Text, CipherText = cipher, OriginDeviceId = "dev-1" },
            });

        var keys = new Mock<IClipboardKeyStore>();
        keys.Setup(k => k.LoadTextKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(key);

        ClipboardEntry? pasted = null;
        var sink = new Mock<IClipboardSink>();
        sink.Setup(s => s.PasteIntoFocusedAsync(It.IsAny<ClipboardEntry>(), It.IsAny<CancellationToken>()))
            .Callback<ClipboardEntry, CancellationToken>((e, _) => pasted = e)
            .ReturnsAsync(true);

        var actions = BuildEngine(transport, sink: sink, keys: keys);
        var viewerClosed = 0;
        actions.CloseClipboardViewer = () => viewerClosed++;

        var found = await actions.PasteClipboardEntryAsync("i1", CancellationToken.None);

        found.Should().BeTrue();
        pasted.Should().NotBeNull();
        pasted!.Text.Should().Be("paste me"); // the sink receives DECRYPTED plaintext
        viewerClosed.Should().Be(1);
    }

    [Fact]
    public async Task PasteClipboardEntry_text_not_decryptable_yet_fails_and_keeps_viewer_open()
    {
        // Text item with ciphertext but no admitted key -> plaintext stays null. Pasting must report
        // failure (no-op) and leave the viewer open rather than dismissing with a false "ok".
        var transport = new Mock<IClipboardTransport>();
        transport.Setup(t => t.GetHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ClipboardEntry>)new[]
            {
                new ClipboardEntry { Id = "i1", Type = ClipboardEntryType.Text, CipherText = new byte[] { 1, 2, 3 }, OriginDeviceId = "dev-1" },
            });

        var keys = new Mock<IClipboardKeyStore>();
        keys.Setup(k => k.LoadTextKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync((byte[]?)null);

        var sink = new Mock<IClipboardSink>();
        var actions = BuildEngine(transport, sink: sink, keys: keys);
        var viewerClosed = 0;
        actions.CloseClipboardViewer = () => viewerClosed++;

        var found = await actions.PasteClipboardEntryAsync("i1", CancellationToken.None);

        found.Should().BeFalse();
        viewerClosed.Should().Be(0);
        sink.Verify(s => s.PasteIntoFocusedAsync(It.IsAny<ClipboardEntry>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PasteClipboardEntry_routes_through_the_clipboard_service_seam()
    {
        var transport = new Mock<IClipboardTransport>();
        transport.Setup(t => t.GetHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ClipboardEntry>)new[]
            {
                new ClipboardEntry { Id = "i1", Type = ClipboardEntryType.Text, Text = "ready", OriginDeviceId = "dev-1" },
            });

        var sink = new Mock<IClipboardSink>();
        var actions = BuildEngine(transport, sink: sink);

        ClipboardEntry? seamEntry = null;
        actions.PasteThroughClipboardService = (e, _) => { seamEntry = e; return Task.FromResult(true); };

        var found = await actions.PasteClipboardEntryAsync("i1", CancellationToken.None);

        found.Should().BeTrue();
        seamEntry.Should().NotBeNull();
        seamEntry!.Text.Should().Be("ready");
        // The seam was used; the raw sink must NOT be called directly.
        sink.Verify(s => s.PasteIntoFocusedAsync(It.IsAny<ClipboardEntry>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SetClipboardHotkey_reregisters_and_persists()
    {
        var hotkey = new Mock<IClipboardHotkey>();
        var transport = new Mock<IClipboardTransport>();

        var actions = BuildEngine(transport, hotkey: hotkey);
        actions.InitializeClipboard(new ClipboardSettings { ViewerHotkey = "Ctrl+Win+Q" }, "dev-1", "Studio PC");

        await actions.SetClipboardHotkeyAsync("Ctrl+Shift+V", CancellationToken.None);

        hotkey.Verify(h => h.Register("Ctrl+Shift+V"), Times.Once);
        actions.CurrentClipboardSettings.ViewerHotkey.Should().Be("Ctrl+Shift+V");
        transport.Verify(t => t.UpdateSettingsAsync(
            "dev-1", It.Is<ClipboardSettings>(s => s.ViewerHotkey == "Ctrl+Shift+V"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SetClipboardHotkey_blank_throws_and_does_not_register()
    {
        var hotkey = new Mock<IClipboardHotkey>();
        var actions = BuildEngine(hotkey: hotkey);

        Func<Task> act = () => actions.SetClipboardHotkeyAsync("   ", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        hotkey.Verify(h => h.Register(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CloseClipboardViewer_invokes_the_close_callback()
    {
        var actions = BuildEngine();
        var closed = 0;
        actions.CloseClipboardViewer = () => closed++;

        await actions.CloseClipboardViewerAsync(CancellationToken.None);

        closed.Should().Be(1);
    }
}
