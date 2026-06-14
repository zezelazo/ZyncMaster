using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ZyncMaster.Server.Tests.Sync;

// Unit tests for the Sync live-push hub. It rides the SAME presence/routing table as the clipboard
// broadcaster (ClipboardConnectionRegistry), so the test scaffolding (capturing / throwing sockets,
// the Conn helper) mirrors ClipboardBroadcasterTests. The contract under test: a recorded run / a
// pair-set change fans out a typed frame to the user's OTHER sessions, never to the origin, never
// across users, and never throws on a dead socket.
public class SyncBroadcasterTests
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

    // A socket whose SendAsync throws, to prove best-effort fan-out.
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

    private static void Conn(ClipboardConnectionRegistry reg, string userId, string deviceId, WebSocket socket) =>
        reg.Add(new ClipboardConnection { UserId = userId, DeviceId = deviceId, Socket = socket });

    private static MirrorResult Result(int created = 2, int updated = 1, int deleted = 0) =>
        new() { Created = created, Updated = updated, Deleted = deleted };

    private static readonly DateTimeOffset RunUtc = new(2026, 6, 13, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task PairRun_reaches_other_devices_not_origin()
    {
        var reg = new ClipboardConnectionRegistry();
        var origin = new CapturingWebSocket();
        var peer1 = new CapturingWebSocket();
        var peer2 = new CapturingWebSocket();
        Conn(reg, "u1", "d1", origin);
        Conn(reg, "u1", "d2", peer1);
        Conn(reg, "u1", "d3", peer2);

        var bc = new SyncBroadcaster(reg);
        await bc.BroadcastPairRunAsync("u1", "d1", "pair-1", Result(), RunUtc, CancellationToken.None);

        // Origin (the device that just ran the pair) never gets its own run echoed back.
        origin.Sent.Should().BeEmpty();
        peer1.Sent.Should().HaveCount(1);
        peer2.Sent.Should().HaveCount(1);

        var frame = JObject.Parse(peer1.Sent[0]);
        frame["type"]!.Value<string>().Should().Be("pair-run");
        frame["pairId"]!.Value<string>().Should().Be("pair-1");
        frame["lastResult"]!["created"]!.Value<int>().Should().Be(2);
        frame["lastResult"]!["updated"]!.Value<int>().Should().Be(1);
        // lastRunUtc serializes to an ISO-8601 string; parse it back rather than cast the JToken
        // (Json.NET surfaces it as a DateTime token, which won't directly cast to DateTimeOffset).
        DateTimeOffset.Parse(
            frame["lastRunUtc"]!.Value<string>()!,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind).Should().Be(RunUtc);
    }

    [Fact]
    public async Task PairRun_with_empty_origin_reaches_every_session()
    {
        // A cookie / identity-bearer human (or the cron context) has no deviceId to exclude, so the
        // run must reach ALL of the user's live sessions.
        var reg = new ClipboardConnectionRegistry();
        var d1 = new CapturingWebSocket();
        var d2 = new CapturingWebSocket();
        Conn(reg, "u1", "d1", d1);
        Conn(reg, "u1", "d2", d2);

        var bc = new SyncBroadcaster(reg);
        await bc.BroadcastPairRunAsync("u1", string.Empty, "pair-1", Result(), RunUtc, CancellationToken.None);

        d1.Sent.Should().HaveCount(1);
        d2.Sent.Should().HaveCount(1);
    }

    [Fact]
    public async Task PairRun_does_not_leak_across_users()
    {
        var reg = new ClipboardConnectionRegistry();
        var mine = new CapturingWebSocket();
        var other = new CapturingWebSocket();
        Conn(reg, "u1", "d2", mine);
        Conn(reg, "u2", "x1", other);

        var bc = new SyncBroadcaster(reg);
        await bc.BroadcastPairRunAsync("u1", "d1", "pair-1", Result(), RunUtc, CancellationToken.None);

        mine.Sent.Should().HaveCount(1);
        other.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task PairRun_carries_partial_and_failures()
    {
        var reg = new ClipboardConnectionRegistry();
        var peer = new CapturingWebSocket();
        Conn(reg, "u1", "d2", peer);

        var result = new MirrorResult { Created = 0, Partial = true, Failures = { "graph 429" } };
        var bc = new SyncBroadcaster(reg);
        await bc.BroadcastPairRunAsync("u1", "d1", "pair-9", result, RunUtc, CancellationToken.None);

        var frame = JObject.Parse(peer.Sent[0]);
        frame["lastResult"]!["partial"]!.Value<bool>().Should().BeTrue();
        frame["lastResult"]!["failures"]!.Values<string>().Should().ContainSingle().Which.Should().Be("graph 429");
    }

    [Fact]
    public async Task PairRun_swallows_a_failing_socket()
    {
        var reg = new ClipboardConnectionRegistry();
        var bad = new ThrowingWebSocket();
        var good = new CapturingWebSocket();
        Conn(reg, "u1", "d2", bad);
        Conn(reg, "u1", "d3", good);

        var bc = new SyncBroadcaster(reg);
        Func<Task> act = () => bc.BroadcastPairRunAsync("u1", "d1", "pair-1", Result(), RunUtc, CancellationToken.None);

        await act.Should().NotThrowAsync();
        good.Sent.Should().HaveCount(1);
        // The dead socket is dropped from the registry so it stops appearing in presence.
        reg.OnlineDeviceIds("u1").Should().NotContain("d2").And.Contain("d3");
    }

    [Fact]
    public async Task PairsChanged_reaches_other_devices_not_origin()
    {
        var reg = new ClipboardConnectionRegistry();
        var origin = new CapturingWebSocket();
        var peer1 = new CapturingWebSocket();
        var peer2 = new CapturingWebSocket();
        Conn(reg, "u1", "d1", origin);
        Conn(reg, "u1", "d2", peer1);
        Conn(reg, "u1", "d3", peer2);

        var bc = new SyncBroadcaster(reg);
        await bc.BroadcastPairsChangedAsync("u1", "d1", CancellationToken.None);

        origin.Sent.Should().BeEmpty();
        peer1.Sent.Should().HaveCount(1);
        peer2.Sent.Should().HaveCount(1);

        var frame = JObject.Parse(peer1.Sent[0]);
        frame["type"]!.Value<string>().Should().Be("pairs-changed");
    }

    [Fact]
    public async Task PairsChanged_with_empty_origin_reaches_every_session()
    {
        var reg = new ClipboardConnectionRegistry();
        var d1 = new CapturingWebSocket();
        var d2 = new CapturingWebSocket();
        Conn(reg, "u1", "d1", d1);
        Conn(reg, "u1", "d2", d2);

        var bc = new SyncBroadcaster(reg);
        await bc.BroadcastPairsChangedAsync("u1", string.Empty, CancellationToken.None);

        d1.Sent.Should().HaveCount(1);
        d2.Sent.Should().HaveCount(1);
    }

    [Fact]
    public async Task PairsChanged_does_not_leak_across_users()
    {
        var reg = new ClipboardConnectionRegistry();
        var mine = new CapturingWebSocket();
        var other = new CapturingWebSocket();
        Conn(reg, "u1", "d2", mine);
        Conn(reg, "u2", "x1", other);

        var bc = new SyncBroadcaster(reg);
        await bc.BroadcastPairsChangedAsync("u1", "d1", CancellationToken.None);

        mine.Sent.Should().HaveCount(1);
        other.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task PairsChanged_swallows_a_failing_socket()
    {
        var reg = new ClipboardConnectionRegistry();
        var bad = new ThrowingWebSocket();
        var good = new CapturingWebSocket();
        Conn(reg, "u1", "d2", bad);
        Conn(reg, "u1", "d3", good);

        var bc = new SyncBroadcaster(reg);
        Func<Task> act = () => bc.BroadcastPairsChangedAsync("u1", "d1", CancellationToken.None);

        await act.Should().NotThrowAsync();
        good.Sent.Should().HaveCount(1);
    }

    [Fact]
    public void Constructor_rejects_null_registry()
    {
        var act = () => new SyncBroadcaster(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task PairRun_rejects_null_arguments()
    {
        var bc = new SyncBroadcaster(new ClipboardConnectionRegistry());

        await FluentActions.Awaiting(() => bc.BroadcastPairRunAsync(null!, "d", "p", Result(), RunUtc, CancellationToken.None))
            .Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => bc.BroadcastPairRunAsync("u", "d", null!, Result(), RunUtc, CancellationToken.None))
            .Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => bc.BroadcastPairRunAsync("u", "d", "p", null!, RunUtc, CancellationToken.None))
            .Should().ThrowAsync<ArgumentNullException>();
    }
}
