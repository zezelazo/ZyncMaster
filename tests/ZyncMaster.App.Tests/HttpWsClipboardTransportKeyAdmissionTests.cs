using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using ZyncMaster.App.Infrastructure.Clipboard;
using ZyncMaster.Engine;
using Xunit;

namespace ZyncMaster.App.Tests;

// Covers the key-admission surface of HttpWsClipboardTransport: the settings PATCH carries the
// publicKeyBase64/needsTextKey advertisement ONLY when set (merge semantics — a plain preferences
// save must not wipe the stored values), the settings GET and the "settings" WS frame map the
// fields back, and GET /api/clipboard/devices is parsed into the key-admission roster. Same
// stubbed-HttpMessageHandler technique as HttpWsClipboardTransportPublishTests.
public class HttpWsClipboardTransportKeyAdmissionTests
{
    // Captures the last request and answers with a canned JSON body.
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string ResponseJson = "";
        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public JObject? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            if (request.Content is not null)
            {
                var raw = await request.Content.ReadAsStringAsync(ct);
                Body = string.IsNullOrWhiteSpace(raw) ? null : JObject.Parse(raw);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ResponseJson) };
        }
    }

    private const string BaseUrl = "https://server.example";

    private static HttpWsClipboardTransport Build(HttpClient http) =>
        new(http, BaseUrl, _ => Task.FromResult("api-key-123"));

    [Fact]
    public async Task UpdateSettings_omits_key_admission_fields_when_unset()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var transport = Build(http);

        await transport.UpdateSettingsAsync("dev-1", new ClipboardSettings { Send = false });

        handler.Method.Should().Be(HttpMethod.Patch);
        handler.Uri!.AbsolutePath.Should().Be("/api/clipboard/settings/dev-1");

        // Null advertisement fields must be ABSENT (not null-valued) so the server merge keeps the
        // stored public key / pending flag through a plain preferences save.
        var body = handler.Body!;
        body.ContainsKey("publicKeyBase64").Should().BeFalse();
        body.ContainsKey("needsTextKey").Should().BeFalse();
        body["send"]!.Value<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task UpdateSettings_sends_key_admission_fields_when_set()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var transport = Build(http);

        await transport.UpdateSettingsAsync("dev-1", new ClipboardSettings
        {
            PublicKeyBase64 = "cHViLWtleQ==",
            NeedsTextKey = true,
        });

        var body = handler.Body!;
        body["publicKeyBase64"]!.Value<string>().Should().Be("cHViLWtleQ==");
        body["needsTextKey"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task GetSettings_maps_key_admission_fields()
    {
        var handler = new CapturingHandler
        {
            ResponseJson = "{\"autoSync\":true,\"send\":true,\"receive\":false,\"viewerHotkey\":\"Ctrl+Win+Q\"," +
                           "\"density\":\"rich\",\"showHints\":true,\"publicKeyBase64\":\"cGs=\",\"needsTextKey\":true}",
        };
        using var http = new HttpClient(handler);
        var transport = Build(http);

        var settings = await transport.GetSettingsAsync("dev-1");

        settings.PublicKeyBase64.Should().Be("cGs=");
        settings.NeedsTextKey.Should().BeTrue();
        settings.Receive.Should().BeFalse();
    }

    [Fact]
    public async Task GetDevices_parses_roster_and_skips_rows_without_a_device_id()
    {
        var handler = new CapturingHandler
        {
            ResponseJson = "[" +
                "{\"deviceId\":\"dev-1\",\"name\":\"Studio PC\",\"online\":true,\"needsTextKey\":false,\"publicKeyBase64\":null}," +
                "{\"deviceId\":\"dev-2\",\"name\":\"Laptop\",\"online\":false,\"needsTextKey\":true,\"publicKeyBase64\":\"cGVlcg==\"}," +
                "{\"name\":\"ghost row without id\"}" +
                "]",
        };
        using var http = new HttpClient(handler);
        var transport = Build(http);

        var devices = await transport.GetDevicesAsync();

        handler.Method.Should().Be(HttpMethod.Get);
        handler.Uri!.AbsolutePath.Should().Be("/api/clipboard/devices");

        devices.Should().HaveCount(2);
        devices[0].DeviceId.Should().Be("dev-1");
        devices[0].Online.Should().BeTrue();
        devices[0].NeedsTextKey.Should().BeFalse();
        devices[0].PublicKeyBase64.Should().BeNull();

        devices[1].DeviceId.Should().Be("dev-2");
        devices[1].Name.Should().Be("Laptop");
        devices[1].Online.Should().BeFalse();
        devices[1].NeedsTextKey.Should().BeTrue();
        devices[1].PublicKeyBase64.Should().Be("cGVlcg==");
    }

    [Fact]
    public async Task GetDevices_empty_or_blank_response_yields_an_empty_roster()
    {
        var handler = new CapturingHandler { ResponseJson = "" };
        using var http = new HttpClient(handler);
        var transport = Build(http);

        (await transport.GetDevicesAsync()).Should().BeEmpty();
    }

    [Fact]
    public void HandleFrame_settings_broadcast_carries_the_key_admission_fields()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var transport = Build(http);

        (string deviceId, ClipboardSettings settings)? received = null;
        transport.SettingsChanged += (deviceId, settings) => received = (deviceId, settings);

        transport.HandleFrame(
            "{\"type\":\"settings\",\"deviceId\":\"dev-7\",\"settings\":{" +
            "\"autoSync\":true,\"send\":true,\"receive\":true,\"viewerHotkey\":\"Ctrl+Win+Q\"," +
            "\"density\":\"rich\",\"showHints\":true,\"publicKeyBase64\":\"cGVlcg==\",\"needsTextKey\":true}}");

        received.Should().NotBeNull();
        received!.Value.deviceId.Should().Be("dev-7");
        received.Value.settings.NeedsTextKey.Should().BeTrue();
        received.Value.settings.PublicKeyBase64.Should().Be("cGVlcg==");
    }

    [Fact]
    public void HandleFrame_settings_broadcast_without_admission_fields_maps_them_null()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var transport = Build(http);

        ClipboardSettings? received = null;
        transport.SettingsChanged += (_, settings) => received = settings;

        transport.HandleFrame("{\"type\":\"settings\",\"deviceId\":\"dev-7\",\"settings\":{\"send\":false}}");

        received.Should().NotBeNull();
        received!.NeedsTextKey.Should().BeNull();
        received.PublicKeyBase64.Should().BeNull();
        received.Send.Should().BeFalse();
    }
}
