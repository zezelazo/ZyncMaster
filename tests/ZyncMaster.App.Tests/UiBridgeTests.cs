using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ZyncMaster.App.Bridge;
using ZyncMaster.App.State;
using ZyncMaster.Engine;
using Xunit;

namespace ZyncMaster.App.Tests;

public class UiBridgeTests
{
    // A transport the test drives: PushInbound simulates a web->native message;
    // Sent captures everything native pushes back.
    private sealed class FakeTransport : IBridgeTransport
    {
        public List<string> Sent { get; } = new();
        public event Action<string>? Received;
        public void Send(string json) => Sent.Add(json);
        public void PushInbound(string json) => Received?.Invoke(json);
    }

    private sealed class FakeEngineActions : IEngineActions
    {
        public int GetStatusCalls;
        public int SyncNowCalls;
        public int PairCalls;
        public string? SavedConfig;
        public bool? SetPausedValue;
        public Func<Task>? Throw;

        // WS3 capture fields.
        public int ListAccountsCalls;
        public int ListPairsCalls;
        public int GenerateTxtCalls;
        public int GetAutoStartCalls;
        public string? ListCalendarsArg;
        public string? CreatePairArg;
        public string? UpdatePairArg;
        public string? DeletePairArg;
        public string? RunPairNowArg;
        public string? UnlinkAccountArg;
        public bool? SetAutoStartValue;

        public int CheckServerHealthCalls;
        public ServerHealth ServerHealthToReturn = ServerHealth.Healthy;

        public AppStatus StatusToReturn = new() { Status = SyncStatus.Idle, Paired = true };
        public SyncResult SyncResultToReturn = new() { Push = new SyncPushResult { Created = 2 } };
        public PairingOutcome PairingToReturn = new() { Success = true, Code = "9F2A" };

        public IReadOnlyList<AccountInfo> AccountsToReturn = new List<AccountInfo>
        {
            new() { AccountRef = "ref-1", DisplayName = "Personal", IsDefault = true },
        };
        public IReadOnlyList<CalendarInfo> CalendarsToReturn = new List<CalendarInfo>
        {
            new() { Id = "cal-1", DisplayName = "Calendar", IsDefault = true },
        };
        public SyncPair PairToReturn = new() { Id = "p1", Name = "Pair One", State = "active" };
        public IReadOnlyList<SyncPair> PairsToReturn = new List<SyncPair>
        {
            new() { Id = "p1", Name = "Pair One", State = "active" },
        };
        public MirrorResult MirrorToReturn = new() { Created = 3, Updated = 1 };
        public IReadOnlyList<string> AffectedPairIdsToReturn = new List<string> { "p1", "p2" };
        public string? GenerateTxtPathToReturn = "C:/exports/calendar.txt";
        public bool AutoStartToReturn = true;

        public async Task<ServerHealth> CheckServerHealthAsync(CancellationToken ct = default)
        {
            if (Throw != null) await Throw();
            CheckServerHealthCalls++;
            return ServerHealthToReturn;
        }

        public async Task<AppStatus> GetStatusAsync(CancellationToken ct = default)
        {
            if (Throw != null) await Throw();
            GetStatusCalls++;
            return StatusToReturn;
        }

        public async Task<SyncResult> SyncNowAsync(CancellationToken ct = default)
        {
            if (Throw != null) await Throw();
            SyncNowCalls++;
            return SyncResultToReturn;
        }

        public async Task<PairingOutcome> PairAsync(CancellationToken ct = default)
        {
            if (Throw != null) await Throw();
            PairCalls++;
            return PairingToReturn;
        }

        public async Task SaveConfigAsync(string configJson, CancellationToken ct = default)
        {
            if (Throw != null) await Throw();
            SavedConfig = configJson;
        }

        public async Task SetPausedAsync(bool paused, CancellationToken ct = default)
        {
            if (Throw != null) await Throw();
            SetPausedValue = paused;
        }

        public async Task<IReadOnlyList<AccountInfo>> ListAccountsAsync(CancellationToken ct = default)
        {
            if (Throw != null) await Throw();
            ListAccountsCalls++;
            return AccountsToReturn;
        }

        public async Task<IReadOnlyList<CalendarInfo>> ListCalendarsAsync(string accountRef, CancellationToken ct = default)
        {
            if (Throw != null) await Throw();
            ListCalendarsArg = accountRef;
            return CalendarsToReturn;
        }

        public async Task<SyncPair> CreatePairAsync(string requestJson, CancellationToken ct = default)
        {
            if (Throw != null) await Throw();
            CreatePairArg = requestJson;
            return PairToReturn;
        }

        public async Task<IReadOnlyList<SyncPair>> ListPairsAsync(CancellationToken ct = default)
        {
            if (Throw != null) await Throw();
            ListPairsCalls++;
            return PairsToReturn;
        }

        public async Task<SyncPair> UpdatePairAsync(string requestJson, CancellationToken ct = default)
        {
            if (Throw != null) await Throw();
            UpdatePairArg = requestJson;
            return PairToReturn;
        }

        public async Task DeletePairAsync(string id, CancellationToken ct = default)
        {
            if (Throw != null) await Throw();
            DeletePairArg = id;
        }

        public async Task<MirrorResult> RunPairNowAsync(string id, CancellationToken ct = default)
        {
            if (Throw != null) await Throw();
            RunPairNowArg = id;
            return MirrorToReturn;
        }

        public async Task<IReadOnlyList<string>> UnlinkAccountAsync(string accountRef, CancellationToken ct = default)
        {
            if (Throw != null) await Throw();
            UnlinkAccountArg = accountRef;
            return AffectedPairIdsToReturn;
        }

        // Device self-management capture.
        public int GetDeviceCalls;
        public string? RenameDeviceArg;
        public DeviceInfo DeviceToReturn = new() { DeviceId = "dev-1", Name = "Current Name", Platform = "windows" };
        public DeviceInfo RenamedDeviceToReturn = new() { DeviceId = "dev-1", Name = "New Name", Platform = "windows" };

        public async Task<DeviceInfo> GetDeviceAsync(CancellationToken ct = default)
        {
            if (Throw != null) await Throw();
            GetDeviceCalls++;
            return DeviceToReturn;
        }

        public async Task<DeviceInfo> RenameDeviceAsync(string name, CancellationToken ct = default)
        {
            if (Throw != null) await Throw();
            RenameDeviceArg = name;
            return RenamedDeviceToReturn;
        }

        public async Task<string?> GenerateTxtAsync(CancellationToken ct = default)
        {
            if (Throw != null) await Throw();
            GenerateTxtCalls++;
            return GenerateTxtPathToReturn;
        }

        public async Task<bool> GetAutoStartAsync(CancellationToken ct = default)
        {
            if (Throw != null) await Throw();
            GetAutoStartCalls++;
            return AutoStartToReturn;
        }

        public async Task SetAutoStartAsync(bool enabled, CancellationToken ct = default)
        {
            if (Throw != null) await Throw();
            SetAutoStartValue = enabled;
        }

        // Identity capture fields.
        public int GetIdentityStateCalls;
        public int SignOutCalls;
        public string? LoginProviderArg;
        public string? LoginEmailArg;
        public IdentityState IdentityStateToReturn = IdentityState.SignedOut;
        public LoginOutcome LoginOutcomeToReturn = new(true, null, null);

        public async Task<IdentityState> GetIdentityStateAsync(CancellationToken ct = default)
        {
            if (Throw != null) await Throw();
            GetIdentityStateCalls++;
            return IdentityStateToReturn;
        }

        public async Task<LoginOutcome> LoginAsync(string provider, string? email, CancellationToken ct = default)
        {
            if (Throw != null) await Throw();
            LoginProviderArg = provider;
            LoginEmailArg = email;
            return LoginOutcomeToReturn;
        }

        public int CancelLoginCalls;
        public async Task CancelLoginAsync(CancellationToken ct = default)
        {
            if (Throw != null) await Throw();
            CancelLoginCalls++;
        }

        public async Task SignOutAsync(CancellationToken ct = default)
        {
            if (Throw != null) await Throw();
            SignOutCalls++;
        }

        // Calendar-connect capture fields.
        public string? ConnectCalendarScopeArg;
        public int ListCalendarAccountsCalls;
        public ConnectCalendarOutcome ConnectOutcomeToReturn = ConnectCalendarOutcome.Ok();
        public IReadOnlyList<CalendarAccountSummary> CalendarAccountsToReturn = new List<CalendarAccountSummary>
        {
            new("acc-1", "Graph", "microsoft", "me@outlook.com", "ReadWrite", "active", "Personal"),
        };

        public async Task<ConnectCalendarOutcome> ConnectCalendarAsync(string scope, CancellationToken ct = default)
        {
            if (Throw != null) await Throw();
            ConnectCalendarScopeArg = scope;
            return ConnectOutcomeToReturn;
        }

        public async Task<IReadOnlyList<CalendarAccountSummary>> ListCalendarAccountsAsync(CancellationToken ct = default)
        {
            if (Throw != null) await Throw();
            ListCalendarAccountsCalls++;
            return CalendarAccountsToReturn;
        }
    }

    private sealed class FakeWindowControl : IWindowControl
    {
        public int Minimized, Maximized, Closed, Dragged;
        public void Minimize() => Minimized++;
        public void ToggleMaximize() => Maximized++;
        public void Close() => Closed++;
        public void BeginDragMove() => Dragged++;
    }

    private static string Message(string action, string? correlationId, string? payload = null)
    {
        var obj = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["correlationId"] = correlationId,
            ["payload"] = payload,
        };
        return JsonSerializer.Serialize(obj);
    }

    private static JsonElement LastReply(FakeTransport transport)
    {
        transport.Sent.Should().NotBeEmpty();
        return JsonSerializer.Deserialize<JsonElement>(transport.Sent[^1]);
    }

    [Fact]
    public void GetStatus_calls_engine_and_replies_ok_with_correlation_id()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions();
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("getStatus", "c1"));

        engine.GetStatusCalls.Should().Be(1);
        var reply = LastReply(transport);
        reply.GetProperty("correlationId").GetString().Should().Be("c1");
        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
        // Payload is the serialized AppStatus.
        var payload = reply.GetProperty("payload").GetString();
        payload.Should().NotBeNullOrEmpty();
        var status = JsonSerializer.Deserialize<JsonElement>(payload!);
        status.GetProperty("paired").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void CheckServerHealth_calls_engine_and_replies_with_health()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions
        {
            ServerHealthToReturn = ServerHealth.Waking("warming up"),
        };
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("checkServerHealth", "h1"));

        engine.CheckServerHealthCalls.Should().Be(1);
        var reply = LastReply(transport);
        reply.GetProperty("correlationId").GetString().Should().Be("h1");
        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
        var payload = JsonSerializer.Deserialize<JsonElement>(reply.GetProperty("payload").GetString()!);
        payload.GetProperty("ok").GetBoolean().Should().BeFalse();
        payload.GetProperty("status").GetString().Should().Be("waking");
        payload.GetProperty("message").GetString().Should().Be("warming up");
    }

    [Fact]
    public void SyncNow_calls_engine_and_replies_ok()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions();
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("syncNow", "c2"));

        engine.SyncNowCalls.Should().Be(1);
        var reply = LastReply(transport);
        reply.GetProperty("correlationId").GetString().Should().Be("c2");
        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Pair_calls_engine_and_replies_ok()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions();
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("pair", "c3"));

        engine.PairCalls.Should().Be(1);
        var reply = LastReply(transport);
        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
        var payload = JsonSerializer.Deserialize<JsonElement>(reply.GetProperty("payload").GetString()!);
        payload.GetProperty("success").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void SaveConfig_passes_payload_through_to_engine()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions();
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("saveConfig", "c4", "{\"serverBaseUrl\":\"https://x\"}"));

        engine.SavedConfig.Should().Be("{\"serverBaseUrl\":\"https://x\"}");
        LastReply(transport).GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void SetPaused_parses_payload_and_calls_engine()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions();
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("setPaused", "c5", "true"));

        engine.SetPausedValue.Should().BeTrue();
        LastReply(transport).GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Unknown_action_replies_not_ok_with_error()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions();
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("frobnicate", "c6"));

        var reply = LastReply(transport);
        reply.GetProperty("ok").GetBoolean().Should().BeFalse();
        reply.GetProperty("error").GetString().Should().NotBeNullOrEmpty();
        reply.GetProperty("correlationId").GetString().Should().Be("c6");
    }

    [Fact]
    public void Engine_throwing_replies_not_ok_and_no_exception_escapes()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions
        {
            Throw = () => throw new InvalidOperationException("boom"),
        };
        _ = new UiBridge(transport, engine);

        var act = () => transport.PushInbound(Message("syncNow", "c7"));

        act.Should().NotThrow();
        var reply = LastReply(transport);
        reply.GetProperty("ok").GetBoolean().Should().BeFalse();
        reply.GetProperty("error").GetString().Should().Contain("boom");
    }

    [Fact]
    public void Malformed_inbound_json_does_not_throw()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions();
        _ = new UiBridge(transport, engine);

        var act = () => transport.PushInbound("this is not json {");

        act.Should().NotThrow();
    }

    [Fact]
    public void Window_actions_route_to_the_window_control()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions();
        var win = new FakeWindowControl();
        _ = new UiBridge(transport, engine, () => win);

        transport.PushInbound(Message("windowMinimize", null));
        transport.PushInbound(Message("windowToggleMaximize", null));
        transport.PushInbound(Message("windowClose", null));
        transport.PushInbound(Message("windowDragMove", null));

        win.Minimized.Should().Be(1);
        win.Maximized.Should().Be(1);
        win.Closed.Should().Be(1);
        win.Dragged.Should().Be(1);
    }

    [Fact]
    public void PushStatus_sends_a_status_event_message()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions();
        var bridge = new UiBridge(transport, engine);

        bridge.PushStatus(new AppStatus { Status = SyncStatus.Syncing, Paired = true });

        transport.Sent.Should().ContainSingle();
        var msg = JsonSerializer.Deserialize<JsonElement>(transport.Sent[0]);
        msg.GetProperty("event").GetString().Should().Be("status");
        msg.GetProperty("payload").GetProperty("status").GetString().Should().Be("Syncing");
    }

    // ---------------- WS3: sync-pair lifecycle actions ----------------

    [Fact]
    public void ListAccounts_calls_engine_and_replies_ok_with_account_array()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions();
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("listAccounts", "a1"));

        engine.ListAccountsCalls.Should().Be(1);
        var reply = LastReply(transport);
        reply.GetProperty("correlationId").GetString().Should().Be("a1");
        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
        var payload = JsonSerializer.Deserialize<JsonElement>(reply.GetProperty("payload").GetString()!);
        payload.GetArrayLength().Should().Be(1);
        payload[0].GetProperty("accountRef").GetString().Should().Be("ref-1");
    }

    [Fact]
    public void ListCalendars_passes_accountRef_payload_to_engine()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions();
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("listCalendars", "a2", "ref-7"));

        engine.ListCalendarsArg.Should().Be("ref-7");
        var reply = LastReply(transport);
        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
        var payload = JsonSerializer.Deserialize<JsonElement>(reply.GetProperty("payload").GetString()!);
        payload[0].GetProperty("id").GetString().Should().Be("cal-1");
    }

    [Fact]
    public void CreatePair_passes_request_json_and_returns_created_pair()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions();
        _ = new UiBridge(transport, engine);

        var json = "{\"name\":\"My pair\",\"intervalMin\":15}";
        transport.PushInbound(Message("createPair", "a3", json));

        engine.CreatePairArg.Should().Be(json);
        var reply = LastReply(transport);
        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
        var payload = JsonSerializer.Deserialize<JsonElement>(reply.GetProperty("payload").GetString()!);
        payload.GetProperty("id").GetString().Should().Be("p1");
    }

    [Fact]
    public void ListPairs_calls_engine_and_replies_with_pair_array()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions();
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("listPairs", "a4"));

        engine.ListPairsCalls.Should().Be(1);
        var reply = LastReply(transport);
        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
        var payload = JsonSerializer.Deserialize<JsonElement>(reply.GetProperty("payload").GetString()!);
        payload[0].GetProperty("name").GetString().Should().Be("Pair One");
    }

    [Fact]
    public void UpdatePair_passes_request_json_to_engine()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions();
        _ = new UiBridge(transport, engine);

        var json = "{\"id\":\"p1\",\"state\":\"paused\"}";
        transport.PushInbound(Message("updatePair", "a5", json));

        engine.UpdatePairArg.Should().Be(json);
        LastReply(transport).GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void DeletePair_passes_id_payload_to_engine()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions();
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("deletePair", "a6", "p1"));

        engine.DeletePairArg.Should().Be("p1");
        LastReply(transport).GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void RunPairNow_passes_id_and_returns_mirror_result()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions();
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("runPairNow", "a7", "p1"));

        engine.RunPairNowArg.Should().Be("p1");
        var reply = LastReply(transport);
        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
        var payload = JsonSerializer.Deserialize<JsonElement>(reply.GetProperty("payload").GetString()!);
        payload.GetProperty("created").GetInt32().Should().Be(3);
    }

    [Fact]
    public void UnlinkAccount_passes_accountRef_and_returns_affected_pair_ids()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions();
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("unlinkAccount", "a8", "ref-1"));

        engine.UnlinkAccountArg.Should().Be("ref-1");
        var reply = LastReply(transport);
        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
        var payload = JsonSerializer.Deserialize<JsonElement>(reply.GetProperty("payload").GetString()!);
        payload.GetProperty("affectedPairIds").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public void GetDevice_calls_engine_and_returns_device_info()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions();
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("getDevice", "d1"));

        engine.GetDeviceCalls.Should().Be(1);
        var reply = LastReply(transport);
        reply.GetProperty("correlationId").GetString().Should().Be("d1");
        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
        var payload = JsonSerializer.Deserialize<JsonElement>(reply.GetProperty("payload").GetString()!);
        payload.GetProperty("deviceId").GetString().Should().Be("dev-1");
        payload.GetProperty("name").GetString().Should().Be("Current Name");
        payload.GetProperty("platform").GetString().Should().Be("windows");
    }

    [Fact]
    public void RenameDevice_object_payload_passes_name_and_returns_renamed_device()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions();
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("renameDevice", "d2", "{\"name\":\"New Name\"}"));

        engine.RenameDeviceArg.Should().Be("New Name");
        var reply = LastReply(transport);
        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
        var payload = JsonSerializer.Deserialize<JsonElement>(reply.GetProperty("payload").GetString()!);
        payload.GetProperty("name").GetString().Should().Be("New Name");
    }

    [Fact]
    public void RenameDevice_bare_string_payload_is_taken_as_name()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions();
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("renameDevice", "d3", "Laptop"));

        engine.RenameDeviceArg.Should().Be("Laptop");
        LastReply(transport).GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void GenerateTxt_calls_engine_and_returns_saved_path()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions();
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("generateTxt", "a9"));

        engine.GenerateTxtCalls.Should().Be(1);
        var reply = LastReply(transport);
        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
        var payload = JsonSerializer.Deserialize<JsonElement>(reply.GetProperty("payload").GetString()!);
        payload.GetProperty("cancelled").GetBoolean().Should().BeFalse();
        payload.GetProperty("path").GetString().Should().Be("C:/exports/calendar.txt");
    }

    [Fact]
    public void GenerateTxt_cancelled_reports_cancelled_true()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions { GenerateTxtPathToReturn = null };
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("generateTxt", "a10"));

        var reply = LastReply(transport);
        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
        var payload = JsonSerializer.Deserialize<JsonElement>(reply.GetProperty("payload").GetString()!);
        payload.GetProperty("cancelled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void GetAutoStart_calls_engine_and_returns_enabled_flag()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions { AutoStartToReturn = true };
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("getAutoStart", "a11"));

        engine.GetAutoStartCalls.Should().Be(1);
        var reply = LastReply(transport);
        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
        var payload = JsonSerializer.Deserialize<JsonElement>(reply.GetProperty("payload").GetString()!);
        payload.GetProperty("enabled").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void SetAutoStart_parses_payload_and_calls_engine()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions();
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("setAutoStart", "a12", "true"));

        engine.SetAutoStartValue.Should().BeTrue();
        LastReply(transport).GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    // ---------------- Identity (sign-in) lifecycle actions ----------------

    [Fact]
    public void GetIdentityState_calls_engine_and_returns_state()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions
        {
            IdentityStateToReturn = new IdentityState(true, "u1", "u1@test", "User One", null, "pro"),
        };
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("getIdentityState", "i1"));

        engine.GetIdentityStateCalls.Should().Be(1);
        var reply = LastReply(transport);
        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
        var payload = JsonSerializer.Deserialize<JsonElement>(reply.GetProperty("payload").GetString()!);
        payload.GetProperty("isSignedIn").GetBoolean().Should().BeTrue();
        payload.GetProperty("email").GetString().Should().Be("u1@test");
        payload.GetProperty("plan").GetString().Should().Be("pro");
    }

    [Fact]
    public void Login_microsoft_passes_provider_and_no_email()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions();
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("login", "i2", "{\"provider\":\"microsoft\"}"));

        engine.LoginProviderArg.Should().Be("microsoft");
        engine.LoginEmailArg.Should().BeNull();
        var reply = LastReply(transport);
        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
        var payload = JsonSerializer.Deserialize<JsonElement>(reply.GetProperty("payload").GetString()!);
        payload.GetProperty("success").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Login_magic_link_passes_provider_and_email()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions();
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("login", "i3", "{\"provider\":\"magic-link\",\"email\":\"me@test\"}"));

        engine.LoginProviderArg.Should().Be("magic-link");
        engine.LoginEmailArg.Should().Be("me@test");
        LastReply(transport).GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void SignOut_calls_engine()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions();
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("signOut", "i4"));

        engine.SignOutCalls.Should().Be(1);
        LastReply(transport).GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void New_pair_action_that_throws_replies_not_ok()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions
        {
            Throw = () => throw new InvalidOperationException("pair boom"),
        };
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("listPairs", "a13"));

        var reply = LastReply(transport);
        reply.GetProperty("ok").GetBoolean().Should().BeFalse();
        reply.GetProperty("error").GetString().Should().Contain("pair boom");
    }

    // ---------------- Calendar-account connection lifecycle actions ----------------

    [Fact]
    public void ConnectCalendar_object_payload_passes_scope_and_returns_outcome()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions();
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("connectCalendar", "k1", "{\"scope\":\"read\"}"));

        engine.ConnectCalendarScopeArg.Should().Be("read");
        var reply = LastReply(transport);
        reply.GetProperty("correlationId").GetString().Should().Be("k1");
        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
        var payload = JsonSerializer.Deserialize<JsonElement>(reply.GetProperty("payload").GetString()!);
        payload.GetProperty("connected").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void ConnectCalendar_missing_payload_defaults_scope_to_readwrite()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions();
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("connectCalendar", "k2"));

        engine.ConnectCalendarScopeArg.Should().Be("readwrite");
        LastReply(transport).GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void ConnectCalendar_bare_string_payload_is_taken_as_scope()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions();
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("connectCalendar", "k3", "readwrite"));

        engine.ConnectCalendarScopeArg.Should().Be("readwrite");
        LastReply(transport).GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void ConnectCalendar_failure_outcome_is_serialized_ok_with_error_in_payload()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions
        {
            ConnectOutcomeToReturn = ConnectCalendarOutcome.Fail("Sign in before connecting a calendar account."),
        };
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("connectCalendar", "k4", "{\"scope\":\"readwrite\"}"));

        var reply = LastReply(transport);
        // The bridge reply is still ok=true (the action ran); the business failure rides the payload.
        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
        var payload = JsonSerializer.Deserialize<JsonElement>(reply.GetProperty("payload").GetString()!);
        payload.GetProperty("connected").GetBoolean().Should().BeFalse();
        payload.GetProperty("error").GetString().Should().Contain("Sign in");
    }

    [Fact]
    public void ListCalendarAccounts_calls_engine_and_replies_with_account_array()
    {
        var transport = new FakeTransport();
        var engine = new FakeEngineActions();
        _ = new UiBridge(transport, engine);

        transport.PushInbound(Message("listCalendarAccounts", "k5"));

        engine.ListCalendarAccountsCalls.Should().Be(1);
        var reply = LastReply(transport);
        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
        var payload = JsonSerializer.Deserialize<JsonElement>(reply.GetProperty("payload").GetString()!);
        payload.GetArrayLength().Should().Be(1);
        payload[0].GetProperty("id").GetString().Should().Be("acc-1");
        payload[0].GetProperty("accountEmail").GetString().Should().Be("me@outlook.com");
    }
}
