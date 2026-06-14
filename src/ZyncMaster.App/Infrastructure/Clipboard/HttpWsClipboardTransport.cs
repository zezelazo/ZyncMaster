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
    public event Action<string>? DeletedReceived;
    public event Action<IReadOnlyList<string>>? PresenceChanged;
    public event Action? PresenceReset;
    public event Action<string, ClipboardSettings>? SettingsChanged;

    // Sync push (the Sync module rides this same clipboard socket — see SyncBroadcaster server-side).
    // PairRunReceived carries (pairId, lastResultJson, lastRunUtc); PairsChanged is payload-less.
    public event Action<string, string, string>? PairRunReceived;
    public event Action? PairsChanged;

    // The latest online-device roster the server pushed via a "presence" frame. Null until the first
    // presence frame arrives (so consumers can tell "no presence yet" from "presence says nobody is
    // online" and fall back to the last-seen heuristic accordingly).
    private volatile IReadOnlyList<string>? _onlineDeviceIds;

    // The last online set the server reported, or null when no presence frame has been seen yet.
    public IReadOnlyList<string>? LatestOnlineDeviceIds => _onlineDeviceIds;

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

    public async Task DeleteEntryAsync(string id, CancellationToken ct = default)
    {
        if (id == null) throw new ArgumentNullException(nameof(id));

        var path = $"/api/clipboard/items/{Uri.EscapeDataString(id)}";
        await SendAsync(HttpMethod.Delete, path, null, ct).ConfigureAwait(false);
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

    // The clipboard view of the user's device roster (GET /api/clipboard/devices): id + name, the
    // live online flag from the server's WS registry, and the key-admission advertisement
    // (needsTextKey + publicKeyBase64). A key-holder sweeps this list to find peers waiting for the
    // E2E text key. Rows without a device id are malformed and skipped.
    public async Task<IReadOnlyList<ClipboardDeviceKeyInfo>> GetDevicesAsync(CancellationToken ct = default)
    {
        var token = await SendAsync(HttpMethod.Get, "/api/clipboard/devices", null, ct).ConfigureAwait(false);

        // Tolerate both a bare array and an envelope { devices: [...] }.
        var arr = token as JArray
                  ?? (token as JObject)?["devices"] as JArray
                  ?? new JArray();

        var list = new List<ClipboardDeviceKeyInfo>(arr.Count);
        foreach (var item in arr)
            if (item is JObject obj)
            {
                var id = obj["deviceId"]?.Value<string>();
                if (string.IsNullOrEmpty(id))
                    continue;

                list.Add(new ClipboardDeviceKeyInfo
                {
                    DeviceId = id,
                    Name = obj["name"]?.Value<string>(),
                    Online = obj["online"]?.Value<bool?>() ?? false,
                    NeedsTextKey = obj["needsTextKey"]?.Value<bool?>() ?? false,
                    PublicKeyBase64 = obj["publicKeyBase64"]?.Value<string>(),
                });
            }
        return list;
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
                // Keepalive PINGs surface a dead/half-open connection that ReceiveAsync would otherwise
                // sit on forever (no server frames arrive on a silently-dropped socket). KeepAliveTimeout
                // is the missing-PONG deadline: without it (default Infinite) a truly silent half-open
                // socket — laptop slept, peer powered off with no RST — would NOT fault until the OS TCP
                // timeout (minutes/never). With both set, a missing PONG aborts ReceiveAsync within ~15s,
                // PumpAsync breaks, the loop reconnects, and the App never keeps a phantom-online cache.
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);

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

            // The connection just dropped (PumpAsync returned, or a failure threw) and we are NOT
            // shutting down: the cached presence roster is now stale. Discard it and signal a reset so
            // the devices view falls back to the last-seen heuristic during the reconnect window — a
            // genuinely-online device is rescued by the fallback instead of being stuck "offline".
            _onlineDeviceIds = null;
            PresenceReset?.Invoke();

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

    // internal (not private) so the frame-routing logic can be unit-tested directly without a live
    // socket. The rest of the transport stays an untested process boundary.
    internal void HandleFrame(string json)
    {
        JObject frame;
        try
        {
            // DateParseHandling.None: keep ISO date strings (e.g. a pair-run's lastRunUtc, an item's
            // createdUtc) as RAW strings. The default JObject.Parse coerces them to DateTime tokens,
            // which would reformat lastRunUtc to a locale string before it reaches the UI and make
            // ParseDate's JTokenType.String guard miss createdUtc. This mirrors SendAsync's reader.
            using var reader = new JsonTextReader(new System.IO.StringReader(json))
            {
                DateParseHandling = DateParseHandling.None,
            };
            frame = JObject.Load(reader);
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

            case "deleted":
                // Another device (or the human panel) removed a history entry: drop it from any open
                // list. A frame without an id is malformed and ignored.
                var deletedId = frame["id"]?.Value<string>();
                if (!string.IsNullOrEmpty(deletedId))
                    DeletedReceived?.Invoke(deletedId);
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
                // Server roster broadcast on any device connect/disconnect: cache the live online set
                // and notify so the devices view can prefer it over the last-seen heuristic. A missing/
                // malformed list is treated as an empty roster (everyone offline) rather than ignored,
                // so a genuine "all offline" presence still updates consumers.
                var online = ParseOnlineIds(frame["onlineDeviceIds"] as JArray);
                _onlineDeviceIds = online;
                PresenceChanged?.Invoke(online);
                break;

            case "settings":
                // Server broadcast of a per-device settings change so the user's other open windows
                // update live. A frame without a deviceId is malformed and ignored (we have nothing to
                // attribute the change to); the settings object falls back to defaults per field.
                var settingsDeviceId = frame["deviceId"]?.Value<string>();
                if (!string.IsNullOrEmpty(settingsDeviceId))
                {
                    var settings = MapSettings(frame["settings"] as JObject ?? new JObject());
                    SettingsChanged?.Invoke(settingsDeviceId, settings);
                }
                break;

            case "pair-run":
                // A completed Sync pair run on another of the user's sessions (manual push/run on a
                // peer, a cron RunDue on the VPS, or a sibling machine). The server fans it out here so
                // an open Calendar/Sync screen patches that pair's last-run + result without re-opening
                // the screen. A frame without a pairId is malformed and ignored. lastResult is the
                // MirrorResult object — pass it through as a compact JSON STRING so the UI maps the
                // counts directly without this transport taking a server-model dependency. lastRunUtc
                // is the recorded timestamp (may be absent; an empty string then).
                var runPairId = frame["pairId"]?.Value<string>();
                if (!string.IsNullOrEmpty(runPairId))
                {
                    var lastResultJson = (frame["lastResult"] as JObject)?.ToString(Formatting.None) ?? "{}";
                    var lastRunUtc = frame["lastRunUtc"]?.Value<string>() ?? "";
                    PairRunReceived?.Invoke(runPairId, lastResultJson, lastRunUtc);
                }
                break;

            case "pairs-changed":
                // The user's pair SET changed on another session (create / delete / re-target): a row
                // appears or disappears, so a per-row patch is not enough. Signal a full reload.
                PairsChanged?.Invoke();
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
        PublicKeyBase64 = obj["publicKeyBase64"]?.Value<string>(),
        NeedsTextKey = obj["needsTextKey"]?.Value<bool?>(),
    };

    private static JObject SettingsToJson(ClipboardSettings s)
    {
        var json = new JObject
        {
            ["autoSync"] = s.AutoSync,
            ["send"] = s.Send,
            ["receive"] = s.Receive,
            ["viewerHotkey"] = s.ViewerHotkey,
            ["density"] = s.Density,
            ["showHints"] = s.ShowHints,
        };

        // The key-admission fields are MERGE semantics server-side: send them only when set, so a
        // plain preferences save (both null) leaves the stored public key / needs-key flag alone.
        if (s.PublicKeyBase64 is not null)
            json["publicKeyBase64"] = s.PublicKeyBase64;
        if (s.NeedsTextKey is { } needsTextKey)
            json["needsTextKey"] = needsTextKey;

        return json;
    }

    // Reads the onlineDeviceIds array of a presence frame into a string list, skipping null/blank
    // entries. A null array (field absent) yields an empty list — "nobody online".
    private static IReadOnlyList<string> ParseOnlineIds(JArray? arr)
    {
        if (arr is null)
            return Array.Empty<string>();

        var ids = new List<string>(arr.Count);
        foreach (var token in arr)
        {
            var id = token?.Value<string>();
            if (!string.IsNullOrEmpty(id))
                ids.Add(id);
        }
        return ids;
    }

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
