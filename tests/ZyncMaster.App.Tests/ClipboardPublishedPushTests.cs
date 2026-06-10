using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using ZyncMaster.App.Bridge;
using ZyncMaster.App.Configuration;
using ZyncMaster.Core;
using ZyncMaster.Engine;
using Xunit;

namespace ZyncMaster.App.Tests;

// Own-items visibility fix. The server broadcaster excludes the origin device from the
// "clipboard:item" echo (correct — it prevents loops), so the ONLY way a machine ever sees its own
// copies in the dashboard view / floating viewer is the host mirroring a successful LOCAL publish
// back into the UI. These tests exercise the full chain the App wires in OnClipboardItemPublished:
// ClipboardService.ItemPublished -> EngineActions.ToHistoryItem (textKey null; the local entry is
// already plaintext) -> UiBridge.PushClipboardItem -> a "clipboard:item" envelope on the transport.
// The clipboard transport is mocked; the assertions are that the UI push fires exactly once after a
// real publish, carries the plaintext + this device as origin, and does NOT fire when the capture
// was dropped by the dedupe or the publish failed.
public class ClipboardPublishedPushTests
{
    private sealed class FakeBridgeTransport : IBridgeTransport
    {
        public List<string> Sent { get; } = new();
        public event Action<string>? Received { add { } remove { } }
        public void Send(string json) => Sent.Add(json);
    }

    private sealed class FakeCapture : IClipboardCaptureSource
    {
        public event Action<ClipboardEntry>? Captured;
        public void Start() { }
        public void Stop() { }
        public void Raise(ClipboardEntry e) => Captured?.Invoke(e);
    }

    private sealed class Harness
    {
        public readonly FakeCapture Capture = new();
        public readonly Mock<IClipboardTransport> Transport = new();
        public readonly FakeBridgeTransport BridgeTransport = new();
        public readonly ClipboardService Service;

        public Harness(Exception? publishError = null)
        {
            var publish = Transport.Setup(t => t.PublishAsync(It.IsAny<ClipboardEntry>(), It.IsAny<CancellationToken>()));
            if (publishError is not null)
                publish.ThrowsAsync(publishError);
            else
                publish.Returns(Task.CompletedTask);

            var textKey = TextCrypto.NewKey();
            var keys = new Mock<IClipboardKeyStore>();
            keys.Setup(k => k.LoadTextKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(textKey);

            Service = new ClipboardService(
                Capture, Transport.Object, new Mock<IClipboardSink>().Object, keys.Object,
                new ClipboardKeyExchange(keys.Object, Transport.Object, () => "dev-this"),
                new ClipboardDedupe(), () => new ClipboardSettings(), hardMaxImageBytes: 1_000_000,
                NullAppLogger.Instance);

            var actions = BuildEngine(Transport, keys);
            var bridge = new UiBridge(BridgeTransport, actions);

            // The exact forwarding the App wires in OnClipboardItemPublished: a locally captured
            // entry is plaintext, so it maps with textKey null and goes out as "clipboard:item".
            Service.ItemPublished += entry => bridge.PushClipboardItem(actions.ToHistoryItem(entry, textKey: null));
        }

        public List<JsonElement> ItemEvents() => BridgeTransport.Sent
            .Select(s => JsonSerializer.Deserialize<JsonElement>(s))
            .Where(m => m.TryGetProperty("event", out var e) && e.GetString() == "clipboard:item")
            .ToList();
    }

    private static EngineActions BuildEngine(Mock<IClipboardTransport> transport, Mock<IClipboardKeyStore> keys)
    {
        var settings = new EngineSettings { ServerBaseUrl = "https://server.test" };
        var deviceKeys = new Mock<IDeviceKeyStore>().Object;

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
            new Mock<ICalExportRunner>().Object, new Mock<IClock>().Object,
            new HttpClient(), NullAppLogger.Instance,
            ownedHttp: null,
            clipboardTransport: transport.Object,
            clipboardSink: new Mock<IClipboardSink>().Object,
            clipboardKeys: keys.Object,
            clipboardHotkey: new Mock<IClipboardHotkey>().Object,
            clipboardDevices: new Mock<IClipboardDevicesSource>().Object);
    }

    private static ClipboardEntry OwnText(string text) => new()
    {
        Id = Guid.NewGuid().ToString(),
        Type = ClipboardEntryType.Text,
        Text = text,
        OriginDeviceId = "dev-this",
        OriginDeviceName = "This PC",
        CreatedUtc = DateTimeOffset.UnixEpoch,
    };

    // The capture path is an async-void event boundary: poll instead of a fixed sleep.
    private static async Task WaitForAsync(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < 5000)
            await Task.Delay(20);
    }

    [Fact]
    public async Task LocalCapture_Published_PushesClipboardItem_WithPlaintextAndOwnOrigin()
    {
        var h = new Harness();

        h.Capture.Raise(OwnText("typed on this machine"));
        await WaitForAsync(() => h.ItemEvents().Count > 0);

        var events = h.ItemEvents();
        events.Should().ContainSingle();
        var payload = events[0].GetProperty("payload");
        payload.GetProperty("text").GetString().Should().Be("typed on this machine");
        payload.GetProperty("type").GetString().Should().Be("Text");
        payload.GetProperty("originDeviceId").GetString().Should().Be("dev-this");
        payload.GetProperty("originDeviceName").GetString().Should().Be("This PC");

        h.Transport.Verify(
            t => t.PublishAsync(It.IsAny<ClipboardEntry>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DedupeDroppedDuplicateCapture_DoesNotPushAgain()
    {
        // Windows multi-fires WM_CLIPBOARDUPDATE for one copy; the dedupe collapses the duplicates
        // into a single publish, and the UI mirror must collapse with it — otherwise the open list
        // grows phantom twin rows the shared history does not contain.
        var h = new Harness();

        h.Capture.Raise(OwnText("same content"));
        await WaitForAsync(() => h.ItemEvents().Count > 0);
        h.Capture.Raise(OwnText("same content"));
        h.Capture.Raise(OwnText("same content"));
        await Task.Delay(100); // give the dropped duplicates time to (incorrectly) push

        h.ItemEvents().Should().ContainSingle();
        h.Transport.Verify(
            t => t.PublishAsync(It.IsAny<ClipboardEntry>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FailedPublish_DoesNotPush()
    {
        // The item never reached the shared history, so mirroring it would show this device a row
        // its peers will never receive.
        var h = new Harness(publishError: new InvalidOperationException("413 too large"));

        h.Capture.Raise(OwnText("doomed"));
        await Task.Delay(100);

        h.ItemEvents().Should().BeEmpty();
    }
}
