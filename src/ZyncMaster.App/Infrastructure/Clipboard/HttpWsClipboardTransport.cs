using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ZyncMaster.Engine;

namespace ZyncMaster.App.Infrastructure.Clipboard;

// Device-side IClipboardTransport over the Plan-1 server clipboard surface. REST for publish /
// history / settings / key-relay, and a ClientWebSocket for the live inbound push of items + keys.
// Newtonsoft camelCase + X-Api-Key, mirroring HttpPairsClient. Non-2xx throws SyncClientException.
//
// E2E INVARIANT: a Text entry is published with payloadBase64 = base64(entry.CipherText) — the
// CIPHERTEXT only. entry.Text (the plaintext) is NEVER serialized onto the wire. If a Text publish
// arrives without CipherText we SKIP it (nothing leaks) rather than fall back to plaintext.
//
// Untested process boundary (live HTTP + WebSocket).
public sealed class HttpWsClipboardTransport : IClipboardTransport, IDisposable
{
    private const string ApiKeyHeader = "X-Api-Key";

    private readonly HttpClient _http;
    private readonly string _httpBaseUrl;
    private readonly string _wsBaseUrl;
    private readonly Func<CancellationToken, Task<string>> _apiKeyProvider;

    private readonly CancellationTokenSource _lifetime = new();
    private Task? _receiveLoop;
    private volatile bool _disposed;

    public event Action<ClipboardEntry>? ItemReceived;
    public event Action<string, byte[]>? KeyReceived;

    // apiKeyProvider supplies the device api key for the X-Api-Key header (the App's device-key store
    // feeds it), matching how the App's other device-key calls obtain auth.
    public HttpWsClipboardTransport(
        HttpClient http,
        string serverBaseUrl,
        Func<CancellationToken, Task<string>> apiKeyProvider)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (serverBaseUrl == null) throw new ArgumentNullException(nameof(serverBaseUrl));
        _apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));

        _httpBaseUrl = serverBaseUrl.TrimEnd('/');
        _wsBaseUrl = ToWebSocketScheme(_httpBaseUrl);
    }

    // ----- REST -----

    public async Task PublishAsync(ClipboardEntry encrypted, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(encrypted);

        var body = new JObject
        {
            ["id"] = encrypted.Id,
            ["type"] = encrypted.Type.ToString(), // "Text" / "Image"
            ["originDeviceId"] = encrypted.OriginDeviceId,
            ["originDeviceName"] = encrypted.OriginDeviceName,
            ["sizeBytes"] = encrypted.SizeBytes,
        };

        if (encrypted.Type == ClipboardEntryType.Text)
        {
            // CRITICAL: only the ciphertext ever leaves the device. No CipherText -> skip silently.
            if (encrypted.CipherText is not { Length: > 0 })
                return;
            body["payloadBase64"] = Convert.ToBase64String(encrypted.CipherText);
        }
        else
        {
            if (encrypted.ImageBytes is { Length: > 0 })
                body["payloadBase64"] = Convert.ToBase64String(encrypted.ImageBytes);
            if (encrypted.Thumbnail is { Length: > 0 })
                body["thumbnailBase64"] = Convert.ToBase64String(encrypted.Thumbnail);
        }

        await SendAsync(HttpMethod.Post, "/api/clipboard/items", body, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ClipboardEntry>> GetHistoryAsync(CancellationToken ct = default)
    {
        var token = await SendAsync(HttpMethod.Get, "/api/clipboard/history", null, ct).ConfigureAwait(false);

        // Tolerate both a bare array and an envelope { items: [...] }.
        var arr = token as JArray
                  ?? (token as JObject)?["items"] as JArray
                  ?? new JArray();

        var list = new List<ClipboardEntry>(arr.Count);
        foreach (var item in arr)
            if (item is JObject obj)
            {
                var entry = MapItem(obj);
                if (entry is not null)
                    list.Add(entry);
            }
        return list;
    }

    public async Task<ClipboardSettings> GetSettingsAsync(string deviceId, CancellationToken ct = default)
    {
        if (deviceId == null) throw new ArgumentNullException(nameof(deviceId));

        var path = $"/api/clipboard/settings/{Uri.EscapeDataString(deviceId)}";
        var root = await SendAsync(HttpMethod.Get, path, null, ct).ConfigureAwait(false) as JObject ?? new JObject();
        return MapSettings(root);
    }

    public async Task UpdateSettingsAsync(string deviceId, ClipboardSettings s, CancellationToken ct = default)
    {
        if (deviceId == null) throw new ArgumentNullException(nameof(deviceId));
        ArgumentNullException.ThrowIfNull(s);

        var path = $"/api/clipboard/settings/{Uri.EscapeDataString(deviceId)}";
        await SendAsync(HttpMethod.Patch, path, SettingsToJson(s), ct).ConfigureAwait(false);
    }

    public async Task<bool> RelayKeyAsync(string fromDeviceId, string targetDeviceId, byte[] wrappedKey, CancellationToken ct = default)
    {
        if (fromDeviceId == null) throw new ArgumentNullException(nameof(fromDeviceId));
        if (targetDeviceId == null) throw new ArgumentNullException(nameof(targetDeviceId));
        ArgumentNullException.ThrowIfNull(wrappedKey);

        var body = new JObject
        {
            ["fromDeviceId"] = fromDeviceId,
            ["targetDeviceId"] = targetDeviceId,
            ["wrappedKeyBase64"] = Convert.ToBase64String(wrappedKey),
        };

        var root = await SendAsync(HttpMethod.Post, "/api/clipboard/key/relay", body, ct).ConfigureAwait(false) as JObject ?? new JObject();
        return root["delivered"]?.Value<bool>() ?? false;
    }

    // ----- WebSocket -----

    public Task ConnectAsync(CancellationToken ct = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(HttpWsClipboardTransport));

        // Idempotent: a single long-lived receive loop owns the socket and reconnects on drop.
        _receiveLoop ??= Task.Run(() => ReceiveLoopAsync(_lifetime.Token), _lifetime.Token);
        return Task.CompletedTask;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        var maxBackoff = TimeSpan.FromSeconds(30);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                var apiKey = await _apiKeyProvider(ct).ConfigureAwait(false);
                socket.Options.SetRequestHeader(ApiKeyHeader, apiKey);

                var uri = new Uri($"{_wsBaseUrl}/ws/clipboard");
                await socket.ConnectAsync(uri, ct).ConfigureAwait(false);

                backoff = TimeSpan.FromSeconds(1); // reset after a clean connect
                await PumpAsync(socket, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // shutting down
            }
            catch
            {
                // Any failure (connect refused, drop, malformed) -> back off and retry.
            }

            if (ct.IsCancellationRequested)
                return;

            try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, maxBackoff.Ticks));
        }
    }

    private async Task PumpAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var message = new StringBuilder();

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            message.Clear();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ct).ConfigureAwait(false); }
                    catch { /* best-effort */ }
                    return;
                }
                message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            HandleFrame(message.ToString());
        }
    }

    private void HandleFrame(string json)
    {
        JObject frame;
        try
        {
            frame = JObject.Parse(json);
        }
        catch (JsonException)
        {
            return; // ignore malformed frames
        }

        switch (frame["type"]?.Value<string>())
        {
            case "item":
                if (frame["item"] is JObject itemObj)
                {
                    var entry = MapItem(itemObj);
                    if (entry is not null)
                        ItemReceived?.Invoke(entry);
                }
                break;

            case "key":
                var from = frame["fromDeviceId"]?.Value<string>();
                var wrappedB64 = frame["wrappedKeyBase64"]?.Value<string>();
                if (!string.IsNullOrEmpty(from) && !string.IsNullOrEmpty(wrappedB64))
                {
                    byte[] wrapped;
                    try { wrapped = Convert.FromBase64String(wrappedB64); }
                    catch (FormatException) { break; }
                    KeyReceived?.Invoke(from, wrapped);
                }
                break;

            case "presence":
                // Online-device roster — not surfaced today; safe to ignore.
                break;
        }
    }

    // ----- Mapping -----

    // Maps a server item to a ClipboardEntry. For Text, payloadBase64 is the CIPHERTEXT and lands in
    // CipherText (Text stays null — the ClipboardService decrypts later). For Image the bytes land in
    // ImageBytes / Thumbnail.
    private static ClipboardEntry? MapItem(JObject obj)
    {
        var typeStr = obj["type"]?.Value<string>();
        if (!Enum.TryParse<ClipboardEntryType>(typeStr, ignoreCase: true, out var type))
            return null;

        var payload = DecodeBase64(obj["payloadBase64"]?.Value<string>());

        var entry = new ClipboardEntry
        {
            Id = obj["id"]?.Value<string>() ?? Guid.NewGuid().ToString("N"),
            Type = type,
            OriginDeviceId = obj["originDeviceId"]?.Value<string>() ?? "",
            OriginDeviceName = obj["originDeviceName"]?.Value<string>(),
            CreatedUtc = ParseDate(obj["createdUtc"]) ?? default,
            SizeBytes = obj["sizeBytes"]?.Value<long?>(),
        };

        if (type == ClipboardEntryType.Text)
            return entry with { CipherText = payload };

        return entry with
        {
            ImageBytes = payload,
            Thumbnail = DecodeBase64(obj["thumbnailBase64"]?.Value<string>()),
        };
    }

    private static ClipboardSettings MapSettings(JObject obj) => new()
    {
        AutoSync = obj["autoSync"]?.Value<bool>() ?? true,
        Send = obj["send"]?.Value<bool>() ?? true,
        Receive = obj["receive"]?.Value<bool>() ?? true,
        ViewerHotkey = obj["viewerHotkey"]?.Value<string>() ?? "Ctrl+Win+Q",
        Density = obj["density"]?.Value<string>() ?? "rich",
        ShowHints = obj["showHints"]?.Value<bool>() ?? true,
    };

    private static JObject SettingsToJson(ClipboardSettings s) => new()
    {
        ["autoSync"] = s.AutoSync,
        ["send"] = s.Send,
        ["receive"] = s.Receive,
        ["viewerHotkey"] = s.ViewerHotkey,
        ["density"] = s.Density,
        ["showHints"] = s.ShowHints,
    };

    private static byte[]? DecodeBase64(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        try { return Convert.FromBase64String(value); }
        catch (FormatException) { return null; }
    }

    private static DateTimeOffset? ParseDate(JToken? token)
    {
        if (token == null || token.Type is not JTokenType.String)
            return null;
        var raw = token.Value<string>();
        return string.IsNullOrWhiteSpace(raw)
            ? null
            : DateTimeOffset.Parse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind);
    }

    // ----- HTTP core (X-Api-Key, SyncClientException on non-2xx) -----

    private async Task<JToken?> SendAsync(HttpMethod method, string path, JObject? body, CancellationToken ct)
    {
        var url = $"{_httpBaseUrl}{path}";
        using var request = new HttpRequestMessage(method, url);

        var apiKey = await _apiKeyProvider(ct).ConfigureAwait(false);
        request.Headers.Add(ApiKeyHeader, apiKey);

        if (body != null)
            request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new SyncClientException(
                $"Clipboard request {method} {url} failed with status {(int)response.StatusCode}: {text}",
                (int)response.StatusCode);

        if (string.IsNullOrWhiteSpace(text))
            return null;

        using var reader = new JsonTextReader(new System.IO.StringReader(text))
        {
            DateParseHandling = DateParseHandling.None,
        };
        return JToken.ReadFrom(reader);
    }

    private static string ToWebSocketScheme(string httpUrl)
    {
        if (httpUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return "wss://" + httpUrl["https://".Length..];
        if (httpUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return "ws://" + httpUrl["http://".Length..];
        return httpUrl; // already a ws/wss base
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try { _lifetime.Cancel(); } catch { /* best-effort */ }
        try { _receiveLoop?.Wait(2000); } catch { /* best-effort */ }
        _lifetime.Dispose();
    }
}
