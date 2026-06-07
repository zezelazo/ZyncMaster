using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ZyncMaster.App.Bridge;
using ZyncMaster.Engine;

namespace ZyncMaster.App.Infrastructure.Clipboard;

// IClipboardDevicesSource over GET /api/devices (the user's device roster) authenticated with the
// device api key (X-Api-Key), mirroring HttpWsClipboardTransport's HTTP core. The server returns
// { id, name, lastSeenUtc, ... } per device; the App derives the online flag from lastSeenUtc.
//
// Untested process boundary (live HTTP).
public sealed class HttpClipboardDevicesSource : IClipboardDevicesSource
{
    private const string ApiKeyHeader = "X-Api-Key";

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public HttpClipboardDevicesSource(HttpClient http, string serverBaseUrl)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (serverBaseUrl == null) throw new ArgumentNullException(nameof(serverBaseUrl));
        _baseUrl = serverBaseUrl.TrimEnd('/');
    }

    public async Task<IReadOnlyList<ClipboardDeviceRow>> ListDevicesAsync(string apiKey, CancellationToken ct = default)
    {
        if (apiKey == null) throw new ArgumentNullException(nameof(apiKey));

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/devices");
        request.Headers.Add(ApiKeyHeader, apiKey);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new SyncClientException(
                $"List devices GET {_baseUrl}/api/devices failed with status {(int)response.StatusCode}: {text}",
                (int)response.StatusCode);

        if (string.IsNullOrWhiteSpace(text))
            return new List<ClipboardDeviceRow>();

        JToken token;
        using (var reader = new JsonTextReader(new System.IO.StringReader(text)) { DateParseHandling = DateParseHandling.None })
            token = JToken.ReadFrom(reader);

        var arr = token as JArray
                  ?? (token as JObject)?["devices"] as JArray
                  ?? new JArray();

        var list = new List<ClipboardDeviceRow>(arr.Count);
        foreach (var item in arr)
            if (item is JObject obj)
                list.Add(new ClipboardDeviceRow
                {
                    Id = obj["id"]?.Value<string>() ?? "",
                    Name = obj["name"]?.Value<string>() ?? "",
                    LastSeenUtc = ParseDate(obj["lastSeenUtc"]),
                });
        return list;
    }

    private static DateTimeOffset? ParseDate(JToken? token)
    {
        if (token == null || token.Type is not JTokenType.String)
            return null;
        var raw = token.Value<string>();
        return string.IsNullOrWhiteSpace(raw)
            ? null
            : DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
