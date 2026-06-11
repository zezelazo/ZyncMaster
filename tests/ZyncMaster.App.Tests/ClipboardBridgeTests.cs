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
        public string? CopyArg;
        public bool CopyToReturn = true;
        public string? DeleteArg;
        public string? HotkeyArg;
        public int CloseViewerCalls;
        public int? PastePanelOpacityArg;

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
        public Task<bool> CopyClipboardEntryAsync(string id, CancellationToken ct = default)
        { CopyArg = id; return Task.FromResult(CopyToReturn); }
        public Task DeleteClipboardEntryAsync(string id, CancellationToken ct = default)
        { DeleteArg = id; return Task.CompletedTask; }
        public Task SetClipboardHotkeyAsync(string hotkey, CancellationToken ct = default)
        { HotkeyArg = hotkey; return Task.CompletedTask; }
        public Task CloseClipboardViewerAsync(CancellationToken ct = default)
        { CloseViewerCalls++; return Task.CompletedTask; }
        public Task SetPastePanelOpacityAsync(int opacity, CancellationToken ct = default)
        { PastePanelOpacityArg = opacity; return Task.CompletedTask; }

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
        public Task<string> GetCalendarDayAsync(string dateIso, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> CreateCalendarEventAsync(string requestJson, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> CreateEventReplicasAsync(string requestJson, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> ListPrefixRulesAsync(CancellationToken ct = default) => throw new NotImplementedException();
        public Task<string> SavePrefixRuleAsync(string ruleJson, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeletePrefixRuleAsync(string ruleId, CancellationToken ct = default) => throw new NotImplementedException();
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
    public void CopyClipboardEntry_passes_id_and_replies_status_ok()
    {
        var transport = new FakeTransport();
        var engine = new ClipboardSpyActions { CopyToReturn = true };
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("copyClipboardEntry", "c1", "i1"));

        engine.CopyArg.Should().Be("i1");
        var payload = JsonSerializer.Deserialize<JsonElement>(LastReply(transport).GetProperty("payload").GetString()!);
        payload.GetProperty("status").GetString().Should().Be("ok");
    }

    [Fact]
    public void CopyClipboardEntry_unknown_id_replies_status_notfound()
    {
        var transport = new FakeTransport();
        var engine = new ClipboardSpyActions { CopyToReturn = false };
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("copyClipboardEntry", "c2", "missing"));

        engine.CopyArg.Should().Be("missing");
        var payload = JsonSerializer.Deserialize<JsonElement>(LastReply(transport).GetProperty("payload").GetString()!);
        payload.GetProperty("status").GetString().Should().Be("notfound");
    }

    [Fact]
    public void DeleteClipboardEntry_passes_id_and_replies_ok()
    {
        var transport = new FakeTransport();
        var engine = new ClipboardSpyActions();
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("deleteClipboardEntry", "d1", "i9"));

        engine.DeleteArg.Should().Be("i9");
        var reply = LastReply(transport);
        reply.GetProperty("correlationId").GetString().Should().Be("d1");
        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void PushClipboardDeleted_sends_a_clipboard_deleted_event_with_id()
    {
        var transport = new FakeTransport();
        var engine = new ClipboardSpyActions();
        var bridge = new UiBridge(transport, engine);

        bridge.PushClipboardDeleted("gone-1");

        transport.Sent.Should().ContainSingle();
        var msg = JsonSerializer.Deserialize<JsonElement>(transport.Sent[0]);
        msg.GetProperty("event").GetString().Should().Be("clipboard:deleted");
        msg.GetProperty("payload").GetProperty("id").GetString().Should().Be("gone-1");
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

    [Fact]
    public void PushClipboardPresence_sends_a_clipboard_presence_event_with_onlineDeviceIds()
    {
        // BUG A fix (push side): the host relays a presence change to the UI as a "re-fetch the roster"
        // signal carrying the current online ids.
        var transport = new FakeTransport();
        var engine = new ClipboardSpyActions();
        var bridge = new UiBridge(transport, engine);

        bridge.PushClipboardPresence(new[] { "a", "b" });

        transport.Sent.Should().ContainSingle();
        var msg = JsonSerializer.Deserialize<JsonElement>(transport.Sent[0]);
        msg.GetProperty("event").GetString().Should().Be("clipboard:presence");
        var online = msg.GetProperty("payload").GetProperty("onlineDeviceIds");
        online.GetArrayLength().Should().Be(2);
        online[0].GetString().Should().Be("a");
        online[1].GetString().Should().Be("b");
    }

    [Fact]
    public void PushClipboardSettings_sends_a_clipboard_settings_event_with_deviceId_and_settingsView()
    {
        // BUG B fix (push side): a sibling-window settings change surfaces to the UI as a
        // "clipboard:settings" event carrying the affected deviceId and the camelCase ToSettingsView.
        var transport = new FakeTransport();
        var engine = new ClipboardSpyActions();
        var bridge = new UiBridge(transport, engine);

        bridge.PushClipboardSettings(
            "dev-9",
            new ZyncMaster.Engine.ClipboardSettings { Density = "mini", Send = false, Receive = true, AutoSync = false, ShowHints = false, ViewerHotkey = "Ctrl+Shift+V" });

        transport.Sent.Should().ContainSingle();
        var msg = JsonSerializer.Deserialize<JsonElement>(transport.Sent[0]);
        msg.GetProperty("event").GetString().Should().Be("clipboard:settings");
        var payload = msg.GetProperty("payload");
        payload.GetProperty("deviceId").GetString().Should().Be("dev-9");
        var settings = payload.GetProperty("settings");
        settings.GetProperty("density").GetString().Should().Be("mini");
        settings.GetProperty("send").GetBoolean().Should().BeFalse();
        settings.GetProperty("receive").GetBoolean().Should().BeTrue();
        settings.GetProperty("autoSync").GetBoolean().Should().BeFalse();
        settings.GetProperty("showHints").GetBoolean().Should().BeFalse();
        settings.GetProperty("viewerHotkey").GetString().Should().Be("Ctrl+Shift+V");
    }

    [Fact]
    public void PushClipboardSettings_throws_on_null_deviceId()
    {
        var transport = new FakeTransport();
        var bridge = new UiBridge(transport, new ClipboardSpyActions());

        Action act = () => bridge.PushClipboardSettings(null!, new ZyncMaster.Engine.ClipboardSettings());

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void PushClipboardSettings_throws_on_null_settings()
    {
        var transport = new FakeTransport();
        var bridge = new UiBridge(transport, new ClipboardSpyActions());

        Action act = () => bridge.PushClipboardSettings("dev-9", null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void PushClipboardKeyChanged_sends_a_clipboard_key_event()
    {
        // The E2E text key just landed: the UI gets a payload-less "clipboard:key" refresh signal —
        // the key itself must never cross the bridge.
        var transport = new FakeTransport();
        var bridge = new UiBridge(transport, new ClipboardSpyActions());

        bridge.PushClipboardKeyChanged();

        transport.Sent.Should().ContainSingle();
        var msg = JsonSerializer.Deserialize<JsonElement>(transport.Sent[0]);
        msg.GetProperty("event").GetString().Should().Be("clipboard:key");
        msg.GetProperty("payload").EnumerateObject().Should().BeEmpty();
    }

    // ---------- EngineActions behaviour over fakes ----------

    private static EngineActions BuildEngine(
        Mock<IClipboardTransport>? transport = null,
        Mock<IClipboardSink>? sink = null,
        Mock<IClipboardKeyStore>? keys = null,
        Mock<IClipboardDevicesSource>? devices = null,
        Mock<IClipboardHotkey>? hotkey = null,
        IDeviceKeyStore? deviceKeyStore = null,
        IClock? clock = null,
        Mock<IPairsClient>? pairs = null,
        ISettingsRepository<AppSettings>? settingsRepo = null,
        string settingsPath = "settings.json",
        ClipboardKeyExchange? clipboardKeyExchange = null)
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
            settingsRepo ?? new Mock<ISettingsRepository<AppSettings>>().Object, new AppSettingsResolver(), settingsPath,
            (pairs ?? new Mock<IPairsClient>()).Object, new Mock<IIdentityTokenCache>().Object,
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
            clipboardDevices: (devices ?? new Mock<IClipboardDevicesSource>()).Object,
            clipboardKeyExchange: clipboardKeyExchange);
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
    public async Task GetHistory_wrong_key_reports_failures_and_re_requests_the_key_after_threshold()
    {
        // The history was encrypted with a key we do NOT hold (both sides self-generated). Each row
        // must degrade to Text=null AND feed the key exchange's suspect-key counter; at three
        // consecutive failures the exchange re-advertises our need (settings upsert with
        // needsTextKey=true + our public key) so a peer relays the right key over ours.
        var senderKey = TextCrypto.NewKey();
        var ourWrongKey = TextCrypto.NewKey();

        var transport = new Mock<IClipboardTransport>();
        transport.Setup(t => t.GetHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ClipboardEntry>)new[]
            {
                new ClipboardEntry { Id = "i1", Type = ClipboardEntryType.Text, CipherText = TextCrypto.Encrypt(senderKey, "a"), OriginDeviceId = "dev-2" },
                new ClipboardEntry { Id = "i2", Type = ClipboardEntryType.Text, CipherText = TextCrypto.Encrypt(senderKey, "b"), OriginDeviceId = "dev-2" },
                new ClipboardEntry { Id = "i3", Type = ClipboardEntryType.Text, CipherText = TextCrypto.Encrypt(senderKey, "c"), OriginDeviceId = "dev-2" },
            });
        transport.Setup(t => t.GetSettingsAsync("dev-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClipboardSettings());
        var upserts = new List<ClipboardSettings>();
        transport.Setup(t => t.UpdateSettingsAsync("dev-1", It.IsAny<ClipboardSettings>(), It.IsAny<CancellationToken>()))
            .Callback<string, ClipboardSettings, CancellationToken>((_, s, _) => { lock (upserts) upserts.Add(s); })
            .Returns(Task.CompletedTask);

        var keys = new Mock<IClipboardKeyStore>();
        keys.Setup(k => k.LoadTextKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(ourWrongKey);
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        keys.Setup(k => k.EnsureDeviceKeypairAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((KeyWrap.ExportPublicKey(rsa), rsa));

        var keyExchange = new ClipboardKeyExchange(keys.Object, transport.Object, () => "dev-1");
        var actions = BuildEngine(transport, keys: keys, clipboardKeyExchange: keyExchange);

        var history = await actions.GetClipboardHistoryAsync(CancellationToken.None);

        history.Should().HaveCount(3);
        history.Should().OnlyContain(i => i.Text == null); // degraded, never leaked

        // The re-request is fire-and-forget from the sync mapper: poll for the upsert.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000) { lock (upserts) { if (upserts.Count > 0) break; } await Task.Delay(20); }

        lock (upserts)
        {
            upserts.Should().ContainSingle();
            upserts[0].NeedsTextKey.Should().BeTrue();
            upserts[0].PublicKeyBase64.Should().Be(Convert.ToBase64String(KeyWrap.ExportPublicKey(rsa)));
        }
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

    [Theory]
    [InlineData(30, 30)]
    [InlineData(-5, 0)]
    [InlineData(140, 100)]
    public async Task GetClipboardDevices_surfaces_clamped_paste_panel_opacity_from_settings(int onDisk, int expected)
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new Mock<IClock>();
        clock.SetupGet(c => c.UtcNow).Returns(now);

        var devices = new Mock<IClipboardDevicesSource>();
        devices.Setup(d => d.ListDevicesAsync("device-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ClipboardDeviceRow>)new[]
            {
                new ClipboardDeviceRow { Id = "dev-1", Name = "Studio PC", LastSeenUtc = now.AddMinutes(-1) },
            });

        var transport = new Mock<IClipboardTransport>();
        transport.Setup(t => t.GetSettingsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClipboardSettings());

        var repo = new Mock<ISettingsRepository<AppSettings>>();
        repo.Setup(r => r.TryLoad("settings.json"))
            .Returns(new AppSettings { ServerBaseUrl = "https://x", PastePanelOpacity = onDisk });

        var actions = BuildEngine(transport, devices: devices, clock: clock.Object, settingsRepo: repo.Object);
        actions.InitializeClipboard(new ClipboardSettings(), "dev-1", "Studio PC");

        var view = await actions.GetClipboardDevicesAsync(CancellationToken.None);

        view.PastePanelOpacity.Should().Be(expected);
    }

    [Fact]
    public async Task GetClipboardDevices_resolves_this_device_on_demand_when_clipboard_not_initialized()
    {
        // The clipboard pipeline never started (InitializeClipboard was not called), so the engine has
        // no cached clipboard device id. The device is still registered (calendar sync works), so the
        // view must resolve THIS device from the registration on demand and mark it — otherwise the UI
        // wrongly shows "this device is not registered yet" on a device that is plainly registered.
        var now = DateTimeOffset.UtcNow;
        var clock = new Mock<IClock>();
        clock.SetupGet(c => c.UtcNow).Returns(now);

        var devices = new Mock<IClipboardDevicesSource>();
        devices.Setup(d => d.ListDevicesAsync("device-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ClipboardDeviceRow>)new[]
            {
                new ClipboardDeviceRow { Id = "dev-1", Name = "Studio PC", LastSeenUtc = now.AddMinutes(-1) },
            });

        var transport = new Mock<IClipboardTransport>();
        transport.Setup(t => t.GetSettingsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClipboardSettings());

        // The registration resolves this device as dev-1 via GetDeviceMeAsync (what GetDeviceAsync calls).
        var pairs = new Mock<IPairsClient>();
        pairs.Setup(p => p.GetDeviceMeAsync("device-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceInfo { DeviceId = "dev-1", Name = "Studio PC" });

        var actions = BuildEngine(transport, devices: devices, clock: clock.Object, pairs: pairs);
        // NOTE: no InitializeClipboard call — _clipboardDeviceId starts empty.

        var view = await actions.GetClipboardDevicesAsync(CancellationToken.None);

        view.ThisDeviceId.Should().Be("dev-1");
        view.Devices.Should().ContainSingle();
        view.Devices[0].IsThis.Should().BeTrue();
        view.Devices[0].Online.Should().BeTrue();
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
    public async Task PresenceReset_clears_cached_roster_and_falls_back_to_lastSeen_window()
    {
        // BUG A fix: when the live socket drops the App must DISCARD the last presence frame so a
        // genuinely-online device is rescued by the 10-min last-seen fallback during the reconnect
        // window (instead of being stuck "offline" because the stale non-null cache bypassed it).
        var now = DateTimeOffset.UtcNow;
        var clock = new Mock<IClock>();
        clock.SetupGet(c => c.UtcNow).Returns(now);

        var devices = new Mock<IClipboardDevicesSource>();
        devices.Setup(d => d.ListDevicesAsync("device-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ClipboardDeviceRow>)new[]
            {
                new ClipboardDeviceRow { Id = "dev-A", Name = "A", LastSeenUtc = now.AddHours(-3) },   // stale -> offline
                new ClipboardDeviceRow { Id = "dev-B", Name = "B", LastSeenUtc = now.AddMinutes(-1) }, // within window
                new ClipboardDeviceRow { Id = "dev-C", Name = "C", LastSeenUtc = now.AddMinutes(-20) },// older than 10min -> offline
            });

        var transport = new Mock<IClipboardTransport>();
        transport.Setup(t => t.GetSettingsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ClipboardSettings());

        var actions = BuildEngine(transport, devices: devices, clock: clock.Object);

        // 1) A presence frame seeds [A,B,C] with only B online.
        transport.Raise(t => t.PresenceChanged += null, (IReadOnlyList<string>)new[] { "dev-B" });

        // 2) The socket drops -> the transport raises a presence RESET (no live roster anymore).
        transport.Raise(t => t.PresenceReset += null);

        var view = await actions.GetClipboardDevicesAsync(CancellationToken.None);

        // The cache is cleared, so the last-seen window governs: B (1 min ago) is rescued ONLINE,
        // while A (3h) and C (20 min, older than the 10-min window) are offline.
        view.Devices.Should().HaveCount(3);
        view.Devices[0].Id.Should().Be("dev-A");
        view.Devices[0].Online.Should().BeFalse();
        view.Devices[1].Id.Should().Be("dev-B");
        view.Devices[1].Online.Should().BeTrue();   // rescued by the fallback
        view.Devices[2].Id.Should().Be("dev-C");
        view.Devices[2].Online.Should().BeFalse();
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
        sink.Verify(s => s.PasteIntoFocusedAsync(It.IsAny<ClipboardEntry>(), It.IsAny<nint>(), It.IsAny<CancellationToken>()), Times.Never);
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
        sink.Setup(s => s.PasteIntoFocusedAsync(It.IsAny<ClipboardEntry>(), It.IsAny<nint>(), It.IsAny<CancellationToken>()))
            .Callback<ClipboardEntry, nint, CancellationToken>((e, _, _) => pasted = e)
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
        sink.Verify(s => s.PasteIntoFocusedAsync(It.IsAny<ClipboardEntry>(), It.IsAny<nint>(), It.IsAny<CancellationToken>()), Times.Never);
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
        actions.PasteThroughClipboardService = (e, _, _) => { seamEntry = e; return Task.FromResult(true); };

        var found = await actions.PasteClipboardEntryAsync("i1", CancellationToken.None);

        found.Should().BeTrue();
        seamEntry.Should().NotBeNull();
        seamEntry!.Text.Should().Be("ready");
        // The seam was used; the raw sink must NOT be called directly.
        sink.Verify(s => s.PasteIntoFocusedAsync(It.IsAny<ClipboardEntry>(), It.IsAny<nint>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PasteClipboardEntry_passes_the_captured_target_window_to_the_sink()
    {
        // The viewer captures the user's real foreground window BEFORE it opens; the paste must aim
        // the synthetic Ctrl+V at THAT handle. At paste time the viewer itself is the foreground
        // window, so a sink that captured the live foreground would paste into the viewer (= nowhere).
        var transport = new Mock<IClipboardTransport>();
        transport.Setup(t => t.GetHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ClipboardEntry>)new[]
            {
                new ClipboardEntry { Id = "i1", Type = ClipboardEntryType.Text, Text = "ready", OriginDeviceId = "dev-1" },
            });

        nint sinkTarget = -1;
        var sink = new Mock<IClipboardSink>();
        sink.Setup(s => s.PasteIntoFocusedAsync(It.IsAny<ClipboardEntry>(), It.IsAny<nint>(), It.IsAny<CancellationToken>()))
            .Callback<ClipboardEntry, nint, CancellationToken>((_, t, _) => sinkTarget = t)
            .ReturnsAsync(true);

        var actions = BuildEngine(transport, sink: sink);
        actions.PasteTargetWindowProvider = () => 0x1234;

        var found = await actions.PasteClipboardEntryAsync("i1", CancellationToken.None);

        found.Should().BeTrue();
        sinkTarget.Should().Be((nint)0x1234);
    }

    [Fact]
    public async Task PasteClipboardEntry_passes_the_captured_target_window_through_the_seam()
    {
        var transport = new Mock<IClipboardTransport>();
        transport.Setup(t => t.GetHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ClipboardEntry>)new[]
            {
                new ClipboardEntry { Id = "i1", Type = ClipboardEntryType.Text, Text = "ready", OriginDeviceId = "dev-1" },
            });

        var actions = BuildEngine(transport);
        actions.PasteTargetWindowProvider = () => 0x5678;

        nint seamTarget = -1;
        actions.PasteThroughClipboardService = (_, t, _) => { seamTarget = t; return Task.FromResult(true); };

        var found = await actions.PasteClipboardEntryAsync("i1", CancellationToken.None);

        found.Should().BeTrue();
        seamTarget.Should().Be((nint)0x5678);
    }

    [Fact]
    public async Task CopyClipboardEntry_known_text_decrypts_then_sets_clipboard_without_pasting_or_closing_viewer()
    {
        var key = TextCrypto.NewKey();
        var cipher = TextCrypto.Encrypt(key, "copy me");

        var transport = new Mock<IClipboardTransport>();
        transport.Setup(t => t.GetHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ClipboardEntry>)new[]
            {
                new ClipboardEntry { Id = "i1", Type = ClipboardEntryType.Text, CipherText = cipher, OriginDeviceId = "dev-1" },
            });

        var keys = new Mock<IClipboardKeyStore>();
        keys.Setup(k => k.LoadTextKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(key);

        ClipboardEntry? written = null;
        var sink = new Mock<IClipboardSink>();
        sink.Setup(s => s.SetAsync(It.IsAny<ClipboardEntry>(), It.IsAny<CancellationToken>()))
            .Callback<ClipboardEntry, CancellationToken>((e, _) => written = e)
            .Returns(Task.CompletedTask);

        var actions = BuildEngine(transport, sink: sink, keys: keys);
        var viewerClosed = 0;
        actions.CloseClipboardViewer = () => viewerClosed++;

        var found = await actions.CopyClipboardEntryAsync("i1", CancellationToken.None);

        found.Should().BeTrue();
        written.Should().NotBeNull();
        written!.Text.Should().Be("copy me"); // the sink receives DECRYPTED plaintext
        // Copy-only: no viewer close, no focus steal, no synthetic Ctrl+V.
        viewerClosed.Should().Be(0);
        sink.Verify(s => s.PasteIntoFocusedAsync(It.IsAny<ClipboardEntry>(), It.IsAny<nint>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CopyClipboardEntry_unknown_id_is_clean_no_op()
    {
        var transport = new Mock<IClipboardTransport>();
        transport.Setup(t => t.GetHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ClipboardEntry>)Array.Empty<ClipboardEntry>());
        var sink = new Mock<IClipboardSink>();

        var actions = BuildEngine(transport, sink: sink);

        var found = await actions.CopyClipboardEntryAsync("nope", CancellationToken.None);

        found.Should().BeFalse();
        sink.Verify(s => s.SetAsync(It.IsAny<ClipboardEntry>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CopyClipboardEntry_routes_through_the_copy_seam_for_echo_suppression()
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
        actions.CopyThroughClipboardService = (e, _) => { seamEntry = e; return Task.CompletedTask; };

        var found = await actions.CopyClipboardEntryAsync("i1", CancellationToken.None);

        found.Should().BeTrue();
        seamEntry.Should().NotBeNull();
        seamEntry!.Text.Should().Be("ready");
        // The seam was used; the raw sink must NOT be called directly.
        sink.Verify(s => s.SetAsync(It.IsAny<ClipboardEntry>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CopyClipboardEntry_image_with_bytes_sets_clipboard()
    {
        var transport = new Mock<IClipboardTransport>();
        transport.Setup(t => t.GetHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ClipboardEntry>)new[]
            {
                new ClipboardEntry { Id = "img1", Type = ClipboardEntryType.Image, ImageBytes = new byte[] { 1, 2, 3, 4 }, OriginDeviceId = "dev-1" },
            });

        ClipboardEntry? written = null;
        var sink = new Mock<IClipboardSink>();
        sink.Setup(s => s.SetAsync(It.IsAny<ClipboardEntry>(), It.IsAny<CancellationToken>()))
            .Callback<ClipboardEntry, CancellationToken>((e, _) => written = e)
            .Returns(Task.CompletedTask);

        var actions = BuildEngine(transport, sink: sink);

        var found = await actions.CopyClipboardEntryAsync("img1", CancellationToken.None);

        found.Should().BeTrue();
        written!.ImageBytes.Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public async Task CopyClipboardEntry_image_without_bytes_is_a_no_op()
    {
        // History rows normally carry the full payload; a defensive guard for an image that somehow
        // has none (nothing would land on the OS clipboard — report failure, not a false "Copied").
        var transport = new Mock<IClipboardTransport>();
        transport.Setup(t => t.GetHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ClipboardEntry>)new[]
            {
                new ClipboardEntry { Id = "img1", Type = ClipboardEntryType.Image, ImageBytes = null, OriginDeviceId = "dev-1" },
            });

        var sink = new Mock<IClipboardSink>();
        var actions = BuildEngine(transport, sink: sink);

        var found = await actions.CopyClipboardEntryAsync("img1", CancellationToken.None);

        found.Should().BeFalse();
        sink.Verify(s => s.SetAsync(It.IsAny<ClipboardEntry>(), It.IsAny<CancellationToken>()), Times.Never);
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

    // ---------- paste-panel opacity (App-local) ----------

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, 0)]
    [InlineData(70, 70)]
    [InlineData(100, 100)]
    [InlineData(250, 100)]
    public async Task SetPastePanelOpacity_clamps_and_persists_without_clobbering_other_fields(int input, int expected)
    {
        // The partial save loads the current settings, sets ONLY the opacity, and writes back — so an
        // existing serverBaseUrl/deviceName survive (mirrors RenameDeviceAsync's config mirror).
        var repo = new Mock<ISettingsRepository<AppSettings>>();
        var onDisk = new AppSettings { ServerBaseUrl = "https://keep.me", DeviceName = "KeepName", PastePanelOpacity = 12 };
        repo.Setup(r => r.TryLoad("settings.json")).Returns(onDisk);
        AppSettings? saved = null;
        repo.Setup(r => r.Save(It.IsAny<AppSettings>(), "settings.json"))
            .Callback<AppSettings, string>((s, _) => saved = s);

        var actions = BuildEngine(settingsRepo: repo.Object);

        await actions.SetPastePanelOpacityAsync(input, CancellationToken.None);

        saved.Should().NotBeNull();
        saved!.PastePanelOpacity.Should().Be(expected);
        saved.ServerBaseUrl.Should().Be("https://keep.me");
        saved.DeviceName.Should().Be("KeepName");
    }

    [Fact]
    public async Task SetPastePanelOpacity_creates_settings_when_none_on_disk()
    {
        var repo = new Mock<ISettingsRepository<AppSettings>>();
        repo.Setup(r => r.TryLoad("settings.json")).Returns((AppSettings?)null);
        AppSettings? saved = null;
        repo.Setup(r => r.Save(It.IsAny<AppSettings>(), "settings.json"))
            .Callback<AppSettings, string>((s, _) => saved = s);

        var actions = BuildEngine(settingsRepo: repo.Object);

        await actions.SetPastePanelOpacityAsync(45, CancellationToken.None);

        saved.Should().NotBeNull();
        saved!.PastePanelOpacity.Should().Be(45);
    }

    // ---------- BUG B: settings broadcast subscription ----------

    [Fact]
    public void ClipboardSettingsChanged_relays_a_transport_settings_event_with_deviceId_and_values()
    {
        // BUG B fix (App side): a server 'settings' broadcast surfaces on the transport as
        // SettingsChanged(deviceId, settings); EngineActions re-raises it so the host can push it to
        // the other open windows.
        var transport = new Mock<IClipboardTransport>();
        var actions = BuildEngine(transport);

        string? gotDeviceId = null;
        ClipboardSettings? gotSettings = null;
        actions.ClipboardSettingsChanged += (id, s) => { gotDeviceId = id; gotSettings = s; };

        transport.Raise(t => t.SettingsChanged += null,
            "dev-9", new ClipboardSettings { Density = "mini", Send = false, AutoSync = false });

        gotDeviceId.Should().Be("dev-9");
        gotSettings.Should().NotBeNull();
        gotSettings!.Density.Should().Be("mini");
        gotSettings.Send.Should().BeFalse();
        gotSettings.AutoSync.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteClipboardEntry_forwards_the_id_to_the_transport()
    {
        var transport = new Mock<IClipboardTransport>();
        transport.Setup(t => t.DeleteEntryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask).Verifiable();

        var actions = BuildEngine(transport);

        await actions.DeleteClipboardEntryAsync("i7", CancellationToken.None);

        transport.Verify(t => t.DeleteEntryAsync("i7", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteClipboardEntry_with_empty_id_throws()
    {
        var actions = BuildEngine();
        Func<Task> act = () => actions.DeleteClipboardEntryAsync("", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public void ClipboardDeleted_relays_a_transport_deleted_event_with_the_id()
    {
        // A server 'deleted' broadcast surfaces on the transport as DeletedReceived(id); EngineActions
        // re-raises ClipboardDeleted so the host can push it to the open clipboard screens.
        var transport = new Mock<IClipboardTransport>();
        var actions = BuildEngine(transport);

        string? got = null;
        actions.ClipboardDeleted += id => got = id;

        transport.Raise(t => t.DeletedReceived += null, "gone-9");

        got.Should().Be("gone-9");
    }

    [Fact]
    public void ClipboardPresenceChanged_fires_with_the_online_ids_on_a_presence_frame()
    {
        // BUG A fix (App side): a server presence frame surfaces on the transport as
        // PresenceChanged(onlineIds); EngineActions re-raises ClipboardPresenceChanged so the host can
        // push a "refresh the roster" signal to the user's other open windows.
        var transport = new Mock<IClipboardTransport>();
        var actions = BuildEngine(transport);

        IReadOnlyList<string>? got = null;
        actions.ClipboardPresenceChanged += ids => got = ids;

        transport.Raise(t => t.PresenceChanged += null, (IReadOnlyList<string>)new[] { "dev-B" });

        got.Should().NotBeNull();
        got.Should().ContainSingle().Which.Should().Be("dev-B");
    }

    [Fact]
    public void ClipboardPresenceChanged_fires_with_an_empty_list_on_a_presence_reset()
    {
        // On a socket drop the transport raises PresenceReset; EngineActions clears the cache and
        // re-raises ClipboardPresenceChanged with an EMPTY roster so the UI recomputes the dots from
        // the last-seen fallback instead of staying frozen on the stale set.
        var transport = new Mock<IClipboardTransport>();
        var actions = BuildEngine(transport);

        IReadOnlyList<string>? got = null;
        actions.ClipboardPresenceChanged += ids => got = ids;

        transport.Raise(t => t.PresenceReset += null);

        got.Should().NotBeNull();
        got.Should().BeEmpty();
    }

    // ---------- HttpWsClipboardTransport.HandleFrame: 'settings' frame ----------

    [Fact]
    public void HandleFrame_settings_frame_raises_SettingsChanged_with_deviceId_and_values()
    {
        var transport = new ZyncMaster.App.Infrastructure.Clipboard.HttpWsClipboardTransport(
            new HttpClient(), "https://server.test", _ => Task.FromResult("k"));

        string? gotDeviceId = null;
        ClipboardSettings? gotSettings = null;
        transport.SettingsChanged += (id, s) => { gotDeviceId = id; gotSettings = s; };

        transport.HandleFrame(
            "{\"type\":\"settings\",\"deviceId\":\"dev-7\",\"settings\":{\"autoSync\":false,\"send\":false,\"receive\":true,\"density\":\"mini\",\"showHints\":false,\"viewerHotkey\":\"Ctrl+Shift+V\"}}");

        gotDeviceId.Should().Be("dev-7");
        gotSettings.Should().NotBeNull();
        gotSettings!.AutoSync.Should().BeFalse();
        gotSettings.Send.Should().BeFalse();
        gotSettings.Receive.Should().BeTrue();
        gotSettings.Density.Should().Be("mini");
        gotSettings.ShowHints.Should().BeFalse();
        gotSettings.ViewerHotkey.Should().Be("Ctrl+Shift+V");
    }

    [Fact]
    public void HandleFrame_settings_frame_missing_deviceId_does_not_raise()
    {
        var transport = new ZyncMaster.App.Infrastructure.Clipboard.HttpWsClipboardTransport(
            new HttpClient(), "https://server.test", _ => Task.FromResult("k"));

        var raised = 0;
        transport.SettingsChanged += (_, _) => raised++;

        transport.HandleFrame("{\"type\":\"settings\",\"settings\":{\"density\":\"mini\"}}");

        raised.Should().Be(0);
    }

    // ---------- HttpWsClipboardTransport.HandleFrame: 'deleted' frame ----------

    [Fact]
    public void HandleFrame_deleted_frame_raises_DeletedReceived_with_the_id()
    {
        var transport = new ZyncMaster.App.Infrastructure.Clipboard.HttpWsClipboardTransport(
            new HttpClient(), "https://server.test", _ => Task.FromResult("k"));

        string? got = null;
        transport.DeletedReceived += id => got = id;

        transport.HandleFrame("{\"type\":\"deleted\",\"id\":\"item-77\"}");

        got.Should().Be("item-77");
    }

    [Fact]
    public void HandleFrame_deleted_frame_missing_id_does_not_raise()
    {
        var transport = new ZyncMaster.App.Infrastructure.Clipboard.HttpWsClipboardTransport(
            new HttpClient(), "https://server.test", _ => Task.FromResult("k"));

        var raised = 0;
        transport.DeletedReceived += _ => raised++;

        transport.HandleFrame("{\"type\":\"deleted\"}");

        raised.Should().Be(0);
    }
}
