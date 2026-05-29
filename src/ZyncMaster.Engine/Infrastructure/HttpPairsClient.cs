using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using ZyncMaster.Core;

namespace ZyncMaster.Engine;

// REST client for the server's pairs/accounts surface. Every request carries the
// device api key in X-Api-Key. Newtonsoft camelCase on the wire.
public sealed class HttpPairsClient : IPairsClient
{
    private const string ApiKeyHeader = "X-Api-Key";

    private static readonly JsonSerializer CamelSerializer = JsonSerializer.Create(new JsonSerializerSettings
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
    });

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public HttpPairsClient(HttpClient http, string serverBaseUrl)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (serverBaseUrl == null) throw new ArgumentNullException(nameof(serverBaseUrl));
        _baseUrl = serverBaseUrl.TrimEnd('/');
    }

    public async Task<IReadOnlyList<AccountInfo>> ListAccountsAsync(string apiKey, CancellationToken ct)
    {
        if (apiKey == null) throw new ArgumentNullException(nameof(apiKey));
        var arr = await SendAsync(HttpMethod.Get, "/api/accounts", apiKey, null, ct) as JArray ?? new JArray();

        var list = new List<AccountInfo>();
        foreach (var item in arr)
            list.Add(new AccountInfo
            {
                AccountRef = item["accountRef"]?.Value<string>() ?? "",
                DisplayName = item["displayName"]?.Value<string>() ?? "",
                IsDefault = item["isDefault"]?.Value<bool>() ?? false,
            });
        return list;
    }

    public async Task<IReadOnlyList<CalendarInfo>> ListCalendarsAsync(string apiKey, string accountRef, CancellationToken ct)
    {
        if (apiKey == null) throw new ArgumentNullException(nameof(apiKey));
        if (accountRef == null) throw new ArgumentNullException(nameof(accountRef));

        var arr = await SendAsync(HttpMethod.Get, $"/api/accounts/{accountRef}/calendars", apiKey, null, ct) as JArray ?? new JArray();

        var list = new List<CalendarInfo>();
        foreach (var item in arr)
            list.Add(new CalendarInfo
            {
                Id = item["id"]?.Value<string>() ?? "",
                DisplayName = item["displayName"]?.Value<string>() ?? "",
                IsDefault = item["isDefault"]?.Value<bool>() ?? false,
                Owner = item["owner"]?.Value<string>(),
            });
        return list;
    }

    public async Task<SyncPair> CreatePairAsync(string apiKey, string name, Endpoint source, Endpoint destination, int intervalMin, CancellationToken ct)
    {
        if (apiKey == null) throw new ArgumentNullException(nameof(apiKey));
        if (name == null) throw new ArgumentNullException(nameof(name));
        if (source == null) throw new ArgumentNullException(nameof(source));
        if (destination == null) throw new ArgumentNullException(nameof(destination));

        var body = new JObject
        {
            ["name"] = name,
            ["source"] = EndpointToJson(source),
            ["destination"] = EndpointToJson(destination),
            ["intervalMin"] = intervalMin,
        };

        var root = await SendAsync(HttpMethod.Post, "/api/pairs", apiKey, body, ct) as JObject ?? new JObject();
        return ParsePair(root);
    }

    public async Task<IReadOnlyList<SyncPair>> ListPairsAsync(string apiKey, CancellationToken ct)
    {
        if (apiKey == null) throw new ArgumentNullException(nameof(apiKey));
        var arr = await SendAsync(HttpMethod.Get, "/api/pairs", apiKey, null, ct) as JArray ?? new JArray();

        var list = new List<SyncPair>();
        foreach (var item in arr)
            if (item is JObject obj)
                list.Add(ParsePair(obj));
        return list;
    }

    public async Task<SyncPair> UpdatePairAsync(string apiKey, string id, string? name, int? intervalMin, string? state, CancellationToken ct)
    {
        if (apiKey == null) throw new ArgumentNullException(nameof(apiKey));
        if (id == null) throw new ArgumentNullException(nameof(id));

        var body = new JObject();
        if (name != null) body["name"] = name;
        if (intervalMin != null) body["intervalMin"] = intervalMin.Value;
        if (state != null) body["state"] = state;

        var root = await SendAsync(HttpMethod.Patch, $"/api/pairs/{id}", apiKey, body, ct) as JObject ?? new JObject();
        return ParsePair(root);
    }

    public async Task DeletePairAsync(string apiKey, string id, CancellationToken ct)
    {
        if (apiKey == null) throw new ArgumentNullException(nameof(apiKey));
        if (id == null) throw new ArgumentNullException(nameof(id));
        await SendAsync(HttpMethod.Delete, $"/api/pairs/{id}", apiKey, null, ct);
    }

    public async Task<MirrorResult> PushPairAsync(string apiKey, string id, IReadOnlyList<AppointmentRecord> events, CancellationToken ct)
    {
        if (apiKey == null) throw new ArgumentNullException(nameof(apiKey));
        if (id == null) throw new ArgumentNullException(nameof(id));
        if (events == null) throw new ArgumentNullException(nameof(events));

        var body = new JObject { ["events"] = JArray.FromObject(events, CamelSerializer) };
        var root = await SendAsync(HttpMethod.Post, $"/api/pairs/{id}/push", apiKey, body, ct) as JObject ?? new JObject();
        return ParseMirrorResult(root);
    }

    public async Task<MirrorResult> RunPairAsync(string apiKey, string id, CancellationToken ct)
    {
        if (apiKey == null) throw new ArgumentNullException(nameof(apiKey));
        if (id == null) throw new ArgumentNullException(nameof(id));

        var root = await SendAsync(HttpMethod.Post, $"/api/pairs/{id}/run", apiKey, new JObject(), ct) as JObject ?? new JObject();
        return ParseMirrorResult(root);
    }

    public async Task<IReadOnlyList<string>> UnlinkAccountAsync(string apiKey, string accountRef, CancellationToken ct)
    {
        if (apiKey == null) throw new ArgumentNullException(nameof(apiKey));
        if (accountRef == null) throw new ArgumentNullException(nameof(accountRef));

        var root = await SendAsync(HttpMethod.Delete, $"/api/accounts/{accountRef}", apiKey, null, ct) as JObject ?? new JObject();

        var ids = new List<string>();
        if (root["affectedPairIds"] is JArray arr)
            foreach (var x in arr)
                ids.Add(x.Value<string>() ?? "");
        return ids;
    }

    private static JObject EndpointToJson(Endpoint e)
    {
        var obj = new JObject
        {
            ["provider"] = e.Provider,
            ["calendarId"] = e.CalendarId,
            ["calendarName"] = e.CalendarName,
        };
        if (e.AccountRef != null) obj["accountRef"] = e.AccountRef;
        return obj;
    }

    private static Endpoint ParseEndpoint(JToken? token)
    {
        if (token is not JObject obj) return new Endpoint();
        return new Endpoint
        {
            Provider = obj["provider"]?.Value<string>() ?? "",
            AccountRef = obj["accountRef"]?.Value<string>(),
            CalendarId = obj["calendarId"]?.Value<string>() ?? "",
            CalendarName = obj["calendarName"]?.Value<string>() ?? "",
        };
    }

    private static SyncPair ParsePair(JObject obj) => new SyncPair
    {
        Id = obj["id"]?.Value<string>() ?? "",
        Name = obj["name"]?.Value<string>() ?? "",
        Source = ParseEndpoint(obj["source"]),
        Destination = ParseEndpoint(obj["destination"]),
        IntervalMin = obj["intervalMin"]?.Value<int>() ?? 0,
        State = obj["state"]?.Value<string>() ?? "",
        LastRunUtc = ParseDateTimeOffset(obj["lastRunUtc"]),
        LastResult = obj["lastResult"] is JObject lr ? ParseMirrorResult(lr) : null,
    };

    private static DateTimeOffset? ParseDateTimeOffset(JToken? token)
    {
        if (token == null || token.Type is not JTokenType.String)
            return null;
        var raw = token.Value<string>();
        return string.IsNullOrWhiteSpace(raw)
            ? null
            : DateTimeOffset.Parse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind);
    }

    private static MirrorResult ParseMirrorResult(JObject obj)
    {
        var failures = new List<string>();
        if (obj["failures"] is JArray arr)
            foreach (var f in arr)
                failures.Add(f.Value<string>() ?? "");

        return new MirrorResult
        {
            Created = obj["created"]?.Value<int>() ?? 0,
            Updated = obj["updated"]?.Value<int>() ?? 0,
            Deleted = obj["deleted"]?.Value<int>() ?? 0,
            Skipped = obj["skipped"]?.Value<int>() ?? 0,
            Failures = failures,
        };
    }

    private async Task<JToken?> SendAsync(HttpMethod method, string path, string apiKey, JObject? body, CancellationToken ct)
    {
        var url = $"{_baseUrl}{path}";
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add(ApiKeyHeader, apiKey);
        if (body != null)
            request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new SyncClientException(
                $"Pairs request {method} {url} failed with status {(int)response.StatusCode}: {text}");

        if (string.IsNullOrWhiteSpace(text))
            return null;

        // DateParseHandling.None keeps date-like strings as JTokenType.String so we
        // can parse offsets ourselves without losing the original zone.
        using var reader = new JsonTextReader(new System.IO.StringReader(text))
        {
            DateParseHandling = DateParseHandling.None,
        };
        return JToken.ReadFrom(reader);
    }
}
