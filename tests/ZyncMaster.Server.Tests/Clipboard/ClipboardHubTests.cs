using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace ZyncMaster.Server.Tests.Clipboard;

// The receive loop is what keeps a clipboard WS alive on the server. With WS keepalive enabled
// (Program.cs UseWebSockets KeepAliveInterval), a half-open socket is detected when the framework's
// periodic ping send faults: the next ReceiveAsync throws a WebSocketException, the loop returns, and
// the endpoint's finally block evicts the gone device from the registry + re-broadcasts presence.
// These tests pin the loop's exit contract (Close frame, faulted socket, cancellation) so that
// eviction can actually run — without it the registry keeps a phantom-online device forever.
public class ClipboardHubTests
{
    // Programmable fake socket: returns a queued sequence of behaviours from ReceiveAsync. Each step
    // is either a result to return or an exception to throw, modelling what the framework surfaces
    // when keepalive detects a dead peer.
    private sealed class ScriptedWebSocket : WebSocket
    {
        private readonly Func<Task<WebSocketReceiveResult>>[] _steps;
        private int _i;
        public int ReceiveCount { get; private set; }

        public ScriptedWebSocket(params Func<Task<WebSocketReceiveResult>>[] steps) => _steps = steps;

        public override WebSocketState State { get; } = WebSocketState.Open;

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            ReceiveCount++;
            var step = _steps[Math.Min(_i, _steps.Length - 1)];
            _i++;
            return step();
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static ClipboardConnection Conn(WebSocket socket) =>
        new() { UserId = "u1", DeviceId = "d1", Socket = socket };

    private static WebSocketReceiveResult Close() =>
        new(0, WebSocketMessageType.Close, endOfMessage: true);

    [Fact]
    public async Task ReceiveLoop_returns_when_peer_sends_close_frame()
    {
        var socket = new ScriptedWebSocket(() => Task.FromResult(Close()));

        // Should complete promptly (not hang) — the endpoint's finally then evicts the device.
        var loop = ClipboardHub.RunReceiveLoopAsync(Conn(socket), CancellationToken.None);
        await loop.WaitAsync(TimeSpan.FromSeconds(5));

        loop.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task ReceiveLoop_returns_when_socket_faults_like_a_half_open_peer()
    {
        // A half-open socket detected by the keepalive ping surfaces as a WebSocketException on the
        // next receive. The loop must swallow it and return so the finally can evict the device.
        var socket = new ScriptedWebSocket(
            () => throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely));

        var loop = ClipboardHub.RunReceiveLoopAsync(Conn(socket), CancellationToken.None);
        await loop.WaitAsync(TimeSpan.FromSeconds(5));

        loop.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task ReceiveLoop_returns_when_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var socket = new ScriptedWebSocket(() => throw new OperationCanceledException());
        cts.Cancel();

        var loop = ClipboardHub.RunReceiveLoopAsync(Conn(socket), cts.Token);
        await loop.WaitAsync(TimeSpan.FromSeconds(5));

        loop.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task ReceiveLoop_evicts_gone_device_when_combined_with_endpoint_finally()
    {
        // End-to-end of the eviction contract at the unit level: run the loop to completion (peer
        // closed), then perform the same registry cleanup the endpoint's finally does, and assert the
        // device is gone from presence. This is the behaviour keepalive guarantees gets reached.
        var registry = new ClipboardConnectionRegistry();
        var socket = new ScriptedWebSocket(() => Task.FromResult(Close()));
        var conn = Conn(socket);
        registry.Add(conn);
        registry.OnlineDeviceIds("u1").Should().Contain("d1");

        await ClipboardHub.RunReceiveLoopAsync(conn, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        registry.Remove(conn.UserId, conn.DeviceId);

        registry.OnlineDeviceIds("u1").Should().NotContain("d1");
        registry.Find("u1", "d1").Should().BeNull();
    }
}
