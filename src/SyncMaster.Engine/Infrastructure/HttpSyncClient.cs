using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using SyncMaster.Core;

namespace SyncMaster.Engine;

public sealed class HttpSyncClient : ISyncClient
{
    private const string ApiKeyHeader = "X-Api-Key";

    // camelCase so the wire shape matches CalExport's Complete JSON and the rest of the API.
    private static readonly JsonSerializer EventSerializer = JsonSerializer.Create(new JsonSerializerSettings
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
    });

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public HttpSyncClient(HttpClient http, string serverBaseUrl)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (serverBaseUrl == null) throw new ArgumentNullException(nameof(serverBaseUrl));
        _baseUrl = serverBaseUrl.TrimEnd('/');
    }

    public async Task<SyncPushResult> PushAsync(string apiKey, IReadOnlyList<AppointmentRecord> events, CancellationToken ct)
    {
        if (apiKey == null) throw new ArgumentNullException(nameof(apiKey));
        if (events == null) throw new ArgumentNullException(nameof(events));

        var url = $"{_baseUrl}/api/sync/calendar";
        var payload = new JObject
        {
            ["events"] = JArray.FromObject(events, EventSerializer),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(ApiKeyHeader, apiKey);

        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        // 409 means the server has no Microsoft account connected yet — not an error,
        // a sync we cannot perform until the user connects an account in the panel.
        if (response.StatusCode == HttpStatusCode.Conflict)
            return new SyncPushResult { NoConnectedAccount = true };

        if (!response.IsSuccessStatusCode)
            throw new SyncClientException(
                $"Sync push to {url} failed with status {(int)response.StatusCode}: {text}");

        var root = string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);

        var failures = new List<string>();
        if (root["failures"] is JArray failuresArr)
            foreach (var f in failuresArr)
                failures.Add(f.Value<string>() ?? "");

        return new SyncPushResult
        {
            Created = root["created"]?.Value<int>() ?? 0,
            Updated = root["updated"]?.Value<int>() ?? 0,
            Deleted = root["deleted"]?.Value<int>() ?? 0,
            Skipped = root["skipped"]?.Value<int>() ?? 0,
            Failures = failures,
        };
    }
}
