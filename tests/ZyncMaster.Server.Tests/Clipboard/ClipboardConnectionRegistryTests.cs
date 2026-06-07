using System;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace ZyncMaster.Server.Tests.Clipboard;

public class ClipboardConnectionRegistryTests
{
    // The registry only stores the socket as an identity holder; no socket method is invoked,
    // so every WebSocket member throws to make any accidental use fail loudly.
    private sealed class FakeWebSocket : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => throw new NotImplementedException();
        public override string? CloseStatusDescription => throw new NotImplementedException();
        public override WebSocketState State => throw new NotImplementedException();
        public override string? SubProtocol => throw new NotImplementedException();
        public override void Abort() => throw new NotImplementedException();
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => throw new NotImplementedException();
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => throw new NotImplementedException();
        public override void Dispose() => throw new NotImplementedException();
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotImplementedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    private static ClipboardConnection Conn(string userId, string deviceId) =>
        new() { UserId = userId, DeviceId = deviceId, Socket = new FakeWebSocket() };

    [Fact]
    public void ForUserExcept_excludes_the_sender_and_returns_the_others()
    {
        var reg = new ClipboardConnectionRegistry();
        reg.Add(Conn("u1", "d1"));
        reg.Add(Conn("u1", "d2"));

        reg.ForUserExcept("u1", "d1").Select(c => c.DeviceId).Should().Equal("d2");
    }

    [Fact]
    public void Remove_drops_the_connection()
    {
        var reg = new ClipboardConnectionRegistry();
        reg.Add(Conn("u1", "d1"));
        reg.Add(Conn("u1", "d2"));

        reg.Remove("u1", "d2");

        reg.OnlineDeviceIds("u1").Should().Equal("d1");
        reg.Find("u1", "d2").Should().BeNull();
    }

    [Fact]
    public void OnlineDeviceIds_reflects_the_current_set()
    {
        var reg = new ClipboardConnectionRegistry();
        reg.OnlineDeviceIds("u1").Should().BeEmpty();

        reg.Add(Conn("u1", "d1"));
        reg.Add(Conn("u1", "d2"));

        reg.OnlineDeviceIds("u1").Should().BeEquivalentTo("d1", "d2");
    }

    [Fact]
    public void Connections_are_isolated_per_user()
    {
        var reg = new ClipboardConnectionRegistry();
        reg.Add(Conn("u1", "d1"));
        reg.Add(Conn("u2", "d2"));

        reg.ForUserExcept("u1", "none").Select(c => c.DeviceId).Should().Equal("d1");
        reg.OnlineDeviceIds("u1").Should().Equal("d1");
        reg.Find("u1", "d2").Should().BeNull(); // u2's device not visible to u1
    }

    [Fact]
    public void Find_returns_the_matching_connection_or_null()
    {
        var reg = new ClipboardConnectionRegistry();
        var c1 = Conn("u1", "d1");
        reg.Add(c1);

        reg.Find("u1", "d1").Should().BeSameAs(c1);
        reg.Find("u1", "missing").Should().BeNull();
        reg.Find("missing-user", "d1").Should().BeNull();
    }

    [Fact]
    public void Add_for_existing_device_replaces_the_connection()
    {
        var reg = new ClipboardConnectionRegistry();
        var first = Conn("u1", "d1");
        var second = Conn("u1", "d1");
        reg.Add(first);
        reg.Add(second);

        reg.OnlineDeviceIds("u1").Should().Equal("d1");
        reg.Find("u1", "d1").Should().BeSameAs(second);
    }
}
