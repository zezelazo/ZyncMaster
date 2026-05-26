using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SyncMaster.App.Bridge;
using SyncMaster.App.State;
using SyncMaster.Engine;
using Xunit;

namespace SyncMaster.App.Tests;

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

        public AppStatus StatusToReturn = new() { Status = SyncStatus.Idle, Paired = true };
        public SyncResult SyncResultToReturn = new() { Push = new SyncPushResult { Created = 2 } };
        public PairingOutcome PairingToReturn = new() { Success = true, Code = "9F2A" };

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
}
