using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ZyncMaster.Server.Tests.Clipboard;

public class ClipboardBroadcasterTests
{
    // Capturing socket: records every text frame sent (decoded UTF-8) and reports Open so the
    // broadcaster treats it as a live connection. Only SendAsync/State are exercised.
    private sealed class CapturingWebSocket : WebSocket
    {
        public List<string> Sent { get; } = new();

        public override WebSocketState State => WebSocketState.Open;
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            Sent.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
            return Task.CompletedTask;
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    // A capturing socket whose SendAsync throws, to prove best-effort fan-out.
    private sealed class ThrowingWebSocket : WebSocket
    {
        public override WebSocketState State => WebSocketState.Open;
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("socket is dead");

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    private static ClipboardConnection Conn(ClipboardConnectionRegistry reg, string userId, string deviceId, WebSocket socket)
    {
        var c = new ClipboardConnection { UserId = userId, DeviceId = deviceId, Socket = socket };
        reg.Add(c);
        return c;
    }

    private static ClipboardItem TextItem(string userId, string originDeviceId, byte[] payload) =>
        new()
        {
            Id = "item-1",
            UserId = userId,
            Type = ClipboardItemType.Text,
            OriginDeviceId = originDeviceId,
            OriginDeviceName = "Laptop",
            CreatedUtc = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero),
            Payload = payload,
        };

    [Fact]
    public async Task BroadcastItem_reaches_other_devices_not_origin()
    {
        var reg = new ClipboardConnectionRegistry();
        var d1 = new CapturingWebSocket();
        var d2 = new CapturingWebSocket();
        var d3 = new CapturingWebSocket();
        Conn(reg, "u1", "d1", d1);
        Conn(reg, "u1", "d2", d2);
        Conn(reg, "u1", "d3", d3);

        var payload = Encoding.UTF8.GetBytes("opaque-ciphertext");
        var item = TextItem("u1", "d1", payload);

        var bc = new ClipboardBroadcaster(reg);
        await bc.BroadcastItemAsync(item, CancellationToken.None);

        d1.Sent.Should().BeEmpty();
        d2.Sent.Should().HaveCount(1);
        d3.Sent.Should().HaveCount(1);

        var frame = JObject.Parse(d2.Sent[0]);
        frame["type"]!.Value<string>().Should().Be("item");
        frame["item"]!["payloadBase64"]!.Value<string>().Should().Be(Convert.ToBase64String(payload));
        frame["item"]!["id"]!.Value<string>().Should().Be("item-1");
        frame["item"]!["originDeviceId"]!.Value<string>().Should().Be("d1");
    }

    [Fact]
    public async Task BroadcastItem_does_not_leak_across_users()
    {
        var reg = new ClipboardConnectionRegistry();
        var d2 = new CapturingWebSocket();
        var other = new CapturingWebSocket();
        Conn(reg, "u1", "d2", d2);
        Conn(reg, "u2", "x1", other);

        var bc = new ClipboardBroadcaster(reg);
        await bc.BroadcastItemAsync(TextItem("u1", "d1", Encoding.UTF8.GetBytes("c")), CancellationToken.None);

        d2.Sent.Should().HaveCount(1);
        other.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task RelayKey_to_online_target_sends_and_returns_true()
    {
        var reg = new ClipboardConnectionRegistry();
        var d2 = new CapturingWebSocket();
        var d3 = new CapturingWebSocket();
        Conn(reg, "u1", "d2", d2);
        Conn(reg, "u1", "d3", d3);

        var wrapped = new byte[] { 1, 2, 3, 4 };
        var env = new WrappedKeyEnvelope { FromDeviceId = "d1", TargetDeviceId = "d2", WrappedKey = wrapped };

        var bc = new ClipboardBroadcaster(reg);
        var ok = await bc.RelayKeyAsync("u1", env, CancellationToken.None);

        ok.Should().BeTrue();
        d2.Sent.Should().HaveCount(1);
        d3.Sent.Should().BeEmpty();

        var frame = JObject.Parse(d2.Sent[0]);
        frame["type"]!.Value<string>().Should().Be("key");
        frame["fromDeviceId"]!.Value<string>().Should().Be("d1");
        frame["wrappedKeyBase64"]!.Value<string>().Should().Be(Convert.ToBase64String(wrapped));
    }

    [Fact]
    public async Task RelayKey_to_offline_target_returns_false_and_sends_nothing()
    {
        var reg = new ClipboardConnectionRegistry();
        var d3 = new CapturingWebSocket();
        Conn(reg, "u1", "d3", d3);

        var env = new WrappedKeyEnvelope { FromDeviceId = "d1", TargetDeviceId = "d2", WrappedKey = new byte[] { 9 } };

        var bc = new ClipboardBroadcaster(reg);
        var ok = await bc.RelayKeyAsync("u1", env, CancellationToken.None);

        ok.Should().BeFalse();
        d3.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task BroadcastPresence_sends_online_ids_to_all()
    {
        var reg = new ClipboardConnectionRegistry();
        var d1 = new CapturingWebSocket();
        var d2 = new CapturingWebSocket();
        Conn(reg, "u1", "d1", d1);
        Conn(reg, "u1", "d2", d2);

        var bc = new ClipboardBroadcaster(reg);
        await bc.BroadcastPresenceAsync("u1", CancellationToken.None);

        d1.Sent.Should().HaveCount(1);
        d2.Sent.Should().HaveCount(1);

        foreach (var sock in new[] { d1, d2 })
        {
            var frame = JObject.Parse(sock.Sent[0]);
            frame["type"]!.Value<string>().Should().Be("presence");
            frame["onlineDeviceIds"]!.Values<string>().Should().BeEquivalentTo("d1", "d2");
        }
    }

    [Fact]
    public async Task BroadcastSettings_reaches_other_devices_not_origin()
    {
        var reg = new ClipboardConnectionRegistry();
        var d1 = new CapturingWebSocket();
        var d2 = new CapturingWebSocket();
        var d3 = new CapturingWebSocket();
        Conn(reg, "u1", "d1", d1);
        Conn(reg, "u1", "d2", d2);
        Conn(reg, "u1", "d3", d3);

        var settings = new ClipboardDeviceSettings
        {
            DeviceId = "d1",
            AutoSync = false,
            Send = true,
            Receive = false,
            ViewerHotkey = "Ctrl+Alt+V",
            Density = "mini",
            ShowHints = false,
        };

        var bc = new ClipboardBroadcaster(reg);
        await bc.BroadcastSettingsAsync("u1", "d1", settings, CancellationToken.None);

        // Origin device never gets its own change echoed back.
        d1.Sent.Should().BeEmpty();
        d2.Sent.Should().HaveCount(1);
        d3.Sent.Should().HaveCount(1);

        var frame = JObject.Parse(d2.Sent[0]);
        frame["type"]!.Value<string>().Should().Be("settings");
        frame["deviceId"]!.Value<string>().Should().Be("d1");
        frame["settings"]!["deviceId"]!.Value<string>().Should().Be("d1");
        frame["settings"]!["autoSync"]!.Value<bool>().Should().BeFalse();
        frame["settings"]!["receive"]!.Value<bool>().Should().BeFalse();
        frame["settings"]!["density"]!.Value<string>().Should().Be("mini");
        frame["settings"]!["viewerHotkey"]!.Value<string>().Should().Be("Ctrl+Alt+V");
    }

    [Fact]
    public async Task BroadcastSettings_does_not_leak_across_users()
    {
        var reg = new ClipboardConnectionRegistry();
        var d2 = new CapturingWebSocket();
        var other = new CapturingWebSocket();
        Conn(reg, "u1", "d2", d2);
        Conn(reg, "u2", "x1", other);

        var settings = new ClipboardDeviceSettings { DeviceId = "d1" };

        var bc = new ClipboardBroadcaster(reg);
        await bc.BroadcastSettingsAsync("u1", "d1", settings, CancellationToken.None);

        d2.Sent.Should().HaveCount(1);
        other.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task BroadcastSettings_swallows_a_failing_socket()
    {
        var reg = new ClipboardConnectionRegistry();
        var bad = new ThrowingWebSocket();
        var good = new CapturingWebSocket();
        Conn(reg, "u1", "d2", bad);
        Conn(reg, "u1", "d3", good);

        var bc = new ClipboardBroadcaster(reg);
        Func<Task> act = () => bc.BroadcastSettingsAsync(
            "u1", "d1", new ClipboardDeviceSettings { DeviceId = "d1" }, CancellationToken.None);

        await act.Should().NotThrowAsync();
        good.Sent.Should().HaveCount(1);
    }

    [Fact]
    public async Task BroadcastItem_swallows_a_failing_socket()
    {
        var reg = new ClipboardConnectionRegistry();
        var bad = new ThrowingWebSocket();
        var good = new CapturingWebSocket();
        Conn(reg, "u1", "d2", bad);
        Conn(reg, "u1", "d3", good);

        var bc = new ClipboardBroadcaster(reg);
        Func<Task> act = () => bc.BroadcastItemAsync(TextItem("u1", "d1", Encoding.UTF8.GetBytes("c")), CancellationToken.None);

        await act.Should().NotThrowAsync();
        good.Sent.Should().HaveCount(1);
    }
}
