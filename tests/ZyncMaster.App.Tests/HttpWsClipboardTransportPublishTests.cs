using System;
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

// Covers HttpWsClipboardTransport.PublishAsync — the device-side serialization of a captured
// ClipboardEntry into the POST /api/clipboard/items request body. This is the path an IMAGE travels
// from capture to the server, so it is the right place to prove an image is not dropped on the wire.
// The live HTTP boundary is stubbed with a capturing HttpMessageHandler (the same technique as
// EngineActionsHealthTests); the WebSocket side is not exercised.
public class HttpWsClipboardTransportPublishTests
{
    // Captures the last request (method, uri, parsed JSON body) and returns 200.
    private sealed class CapturingHandler : HttpMessageHandler
    {
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
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") };
        }
    }

    private const string BaseUrl = "https://server.example";

    private static HttpWsClipboardTransport Build(HttpClient http) =>
        new(http, BaseUrl, _ => Task.FromResult("api-key-123"));

    [Fact]
    public async Task PublishAsync_Image_PostsBytesAndThumbnailToItemsEndpoint()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var transport = Build(http);

        var imageBytes = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var thumbnail = new byte[] { 9, 10, 11 };
        var entry = new ClipboardEntry
        {
            Id = "img-1",
            Type = ClipboardEntryType.Image,
            ImageBytes = imageBytes,
            Thumbnail = thumbnail,
            SizeBytes = imageBytes.Length,
            OriginDeviceId = "dev-a",
            OriginDeviceName = "Laptop",
        };

        await transport.PublishAsync(entry);

        handler.Method.Should().Be(HttpMethod.Post);
        handler.Uri!.AbsolutePath.Should().Be("/api/clipboard/items");

        var body = handler.Body!;
        body["id"]!.Value<string>().Should().Be("img-1");
        body["type"]!.Value<string>().Should().Be("Image");
        body["originDeviceId"]!.Value<string>().Should().Be("dev-a");
        body["sizeBytes"]!.Value<long>().Should().Be(imageBytes.Length);

        // The image bytes and thumbnail must both reach the wire as base64 — this is exactly what was
        // being dropped before. Decode them back to assert the round-trip is byte-exact.
        body["payloadBase64"]!.Value<string>().Should().Be(Convert.ToBase64String(imageBytes));
        body["thumbnailBase64"]!.Value<string>().Should().Be(Convert.ToBase64String(thumbnail));

        // An image carries no text payload — only Text items use the text-cipher field.
        body.ContainsKey("payloadBase64").Should().BeTrue();
    }

    [Fact]
    public async Task PublishAsync_ImageWithoutThumbnail_StillPostsBytes()
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var transport = Build(http);

        var imageBytes = new byte[] { 42, 42, 42 };
        var entry = new ClipboardEntry
        {
            Id = "img-2",
            Type = ClipboardEntryType.Image,
            ImageBytes = imageBytes,
            Thumbnail = null, // a failed/oversize thumbnail decode must NOT block the image publish
            SizeBytes = imageBytes.Length,
            OriginDeviceId = "dev-a",
        };

        await transport.PublishAsync(entry);

        handler.Uri!.AbsolutePath.Should().Be("/api/clipboard/items");
        var body = handler.Body!;
        body["type"]!.Value<string>().Should().Be("Image");
        body["payloadBase64"]!.Value<string>().Should().Be(Convert.ToBase64String(imageBytes));
        body["thumbnailBase64"].Should().BeNull();
    }
}
