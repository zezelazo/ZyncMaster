using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using ZyncMaster.Core;

namespace ZyncMaster.Engine;

// REST client for the server's pairs/accounts surface. Newtonsoft camelCase on the wire.
//
// Two auth modes, matching the server's gating:
//   * ACCOUNTS + PAIRS management calls send the user's IDENTITY BEARER in Authorization: Bearer
//     (the server gates them human-only with RequireCookieOrIdentityBearer);
//   * DEVICE self-management + PushPair send the device API KEY in X-Api-Key.
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

    public async Task<IReadOnlyList<AccountInfo>> ListAccountsAsync(string bearer, CancellationToken ct)
    {
        if (bearer == null) throw new ArgumentNullException(nameof(bearer));
        var arr = await SendBearerAsync(HttpMethod.Get, "/api/accounts", bearer, null, ct) as JArray ?? new JArray();

        var list = new List<AccountInfo>();
        foreach (var item in arr)
            list.Add(new AccountInfo
            {
                AccountRef = item["accountRef"]?.Value<string>() ?? "",
                DisplayName = item["displayName"]?.Value<string>() ?? "",
                Email = item["email"]?.Value<string>() ?? "",
                IsDefault = item["isDefault"]?.Value<bool>() ?? false,
            });
        return list;
    }

    public async Task<IReadOnlyList<CalendarInfo>> ListCalendarsAsync(string bearer, string accountRef, CancellationToken ct)
    {
        if (bearer == null) throw new ArgumentNullException(nameof(bearer));
        if (accountRef == null) throw new ArgumentNullException(nameof(accountRef));

        var arr = await SendBearerAsync(HttpMethod.Get, $"/api/accounts/{Uri.EscapeDataString(accountRef)}/calendars", bearer, null, ct) as JArray ?? new JArray();

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

    public async Task<CalendarInfo> CreateCalendarAsync(string bearer, string accountRef, string name, CancellationToken ct)
    {
        if (bearer == null) throw new ArgumentNullException(nameof(bearer));
        if (accountRef == null) throw new ArgumentNullException(nameof(accountRef));
        if (name == null) throw new ArgumentNullException(nameof(name));

        var body = new JObject { ["name"] = name };
        var obj = await SendBearerAsync(
            HttpMethod.Post,
            $"/api/accounts/{Uri.EscapeDataString(accountRef)}/calendars",
            bearer, body, ct) as JObject ?? new JObject();

        return new CalendarInfo
        {
            Id = obj["id"]?.Value<string>() ?? "",
            DisplayName = obj["displayName"]?.Value<string>() ?? "",
            IsDefault = obj["isDefault"]?.Value<bool>() ?? false,
            Owner = obj["owner"]?.Value<string>(),
        };
    }

    public async Task<SyncPair> CreatePairAsync(string bearer, string name, Endpoint source, Endpoint destination, int intervalMin, CancellationToken ct)
    {
        if (bearer == null) throw new ArgumentNullException(nameof(bearer));
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

        var root = await SendBearerAsync(HttpMethod.Post, "/api/pairs", bearer, body, ct) as JObject ?? new JObject();
        return ParsePair(root);
    }

    public async Task<IReadOnlyList<SyncPair>> ListPairsAsync(string bearer, CancellationToken ct)
    {
        if (bearer == null) throw new ArgumentNullException(nameof(bearer));
        var arr = await SendBearerAsync(HttpMethod.Get, "/api/pairs", bearer, null, ct) as JArray ?? new JArray();

        var list = new List<SyncPair>();
        foreach (var item in arr)
            if (item is JObject obj)
                list.Add(ParsePair(obj));
        return list;
    }

    public async Task<SyncPair> UpdatePairAsync(string bearer, string id, string? name, int? intervalMin, string? state, CancellationToken ct, Endpoint? source = null, Endpoint? destination = null)
    {
        if (bearer == null) throw new ArgumentNullException(nameof(bearer));
        if (id == null) throw new ArgumentNullException(nameof(id));

        var body = new JObject();
        if (name != null) body["name"] = name;
        if (intervalMin != null) body["intervalMin"] = intervalMin.Value;
        if (state != null) body["state"] = state;
        if (source != null) body["source"] = EndpointToJson(source);
        if (destination != null) body["destination"] = EndpointToJson(destination);

        var root = await SendBearerAsync(HttpMethod.Patch, $"/api/pairs/{id}", bearer, body, ct) as JObject ?? new JObject();
        return ParsePair(root);
    }

    public async Task<string> ExportSourceTxtAsync(string bearer, string id, int year, int month, bool includeCancelled, CancellationToken ct)
    {
        if (bearer == null) throw new ArgumentNullException(nameof(bearer));
        if (id == null) throw new ArgumentNullException(nameof(id));

        var body = new JObject
        {
            ["year"] = year,
            ["month"] = month,
            ["includeCancelled"] = includeCancelled,
        };

        // The response is the .txt itself (text/plain), not JSON — read it as the raw body so
        // the exact pipe-delimited content is preserved without JSON escaping/unescaping.
        return await SendBearerRawAsync(HttpMethod.Post, $"/api/pairs/{id}/export-source-txt", bearer, body, ct);
    }

    public async Task<CleanupResult> CleanupDestinationAsync(string bearer, string id, Endpoint oldDestination, CancellationToken ct)
    {
        if (bearer == null) throw new ArgumentNullException(nameof(bearer));
        if (id == null) throw new ArgumentNullException(nameof(id));
        if (oldDestination == null) throw new ArgumentNullException(nameof(oldDestination));

        var body = new JObject { ["destination"] = EndpointToJson(oldDestination) };
        var root = await SendBearerAsync(HttpMethod.Post, $"/api/pairs/{id}/cleanup-destination", bearer, body, ct) as JObject ?? new JObject();

        var failures = new List<string>();
        if (root["failures"] is JArray arr)
            foreach (var f in arr)
                failures.Add(f.Value<string>() ?? "");

        return new CleanupResult
        {
            Deleted = root["deleted"]?.Value<int>() ?? 0,
            Failures = failures,
        };
    }

    public async Task<int> CountManagedAsync(string bearer, string id, Endpoint destination, CancellationToken ct)
    {
        if (bearer == null) throw new ArgumentNullException(nameof(bearer));
        if (id == null) throw new ArgumentNullException(nameof(id));
        if (destination == null) throw new ArgumentNullException(nameof(destination));

        var query =
            $"?provider={Uri.EscapeDataString(destination.Provider ?? "")}" +
            $"&calendarId={Uri.EscapeDataString(destination.CalendarId ?? "")}";
        if (!string.IsNullOrEmpty(destination.AccountRef))
            query += $"&accountRef={Uri.EscapeDataString(destination.AccountRef)}";

        var root = await SendBearerAsync(HttpMethod.Get, $"/api/pairs/{id}/managed-count{query}", bearer, null, ct) as JObject ?? new JObject();
        return root["count"]?.Value<int>() ?? 0;
    }

    public async Task DeletePairAsync(string bearer, string id, CancellationToken ct)
    {
        if (bearer == null) throw new ArgumentNullException(nameof(bearer));
        if (id == null) throw new ArgumentNullException(nameof(id));
        await SendBearerAsync(HttpMethod.Delete, $"/api/pairs/{id}", bearer, null, ct);
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

    public async Task<IReadOnlyList<string>> UnlinkAccountAsync(string bearer, string accountRef, CancellationToken ct)
    {
        if (bearer == null) throw new ArgumentNullException(nameof(bearer));
        if (accountRef == null) throw new ArgumentNullException(nameof(accountRef));

        var root = await SendBearerAsync(HttpMethod.Delete, $"/api/accounts/{Uri.EscapeDataString(accountRef)}", bearer, null, ct) as JObject ?? new JObject();

        var ids = new List<string>();
        if (root["affectedPairIds"] is JArray arr)
            foreach (var x in arr)
                ids.Add(x.Value<string>() ?? "");
        return ids;
    }

    public async Task<DateTimeOffset?> HeartbeatAsync(string apiKey, CancellationToken ct)
    {
        if (apiKey == null) throw new ArgumentNullException(nameof(apiKey));

        // The server reads the deviceId from the api key; the body is empty. Returns the renewed
        // { leaseUntil } so the caller can pace the next heartbeat if it ever wants to.
        var root = await SendAsync(HttpMethod.Post, "/api/devices/heartbeat", apiKey, new JObject(), ct) as JObject ?? new JObject();
        return ParseDateTimeOffset(root["leaseUntil"]);
    }

    public async Task<DeviceInfo> GetDeviceMeAsync(string apiKey, CancellationToken ct)
    {
        if (apiKey == null) throw new ArgumentNullException(nameof(apiKey));

        var root = await SendAsync(HttpMethod.Get, "/api/devices/me", apiKey, null, ct) as JObject ?? new JObject();
        return ParseDeviceInfo(root);
    }

    public async Task<DeviceInfo> RenameDeviceAsync(string apiKey, string name, CancellationToken ct)
    {
        if (apiKey == null) throw new ArgumentNullException(nameof(apiKey));
        if (name == null) throw new ArgumentNullException(nameof(name));

        // The server reads the deviceId from the api key, so the body carries ONLY the name.
        var body = new JObject { ["name"] = name };
        var root = await SendAsync(HttpMethod.Post, "/api/devices/rename", apiKey, body, ct) as JObject ?? new JObject();
        return ParseDeviceInfo(root);
    }

    public async Task<bool> CheckDeviceNameAvailableAsync(string apiKey, string name, CancellationToken ct)
    {
        if (apiKey == null) throw new ArgumentNullException(nameof(apiKey));
        if (name == null) throw new ArgumentNullException(nameof(name));

        // GET with the name in the query string; the server scopes to the caller's device and
        // returns { available: bool } (or { available:false, reason:"invalid" } for a bad name).
        var path = $"/api/devices/name-available?name={Uri.EscapeDataString(name)}";
        var root = await SendAsync(HttpMethod.Get, path, apiKey, null, ct) as JObject ?? new JObject();
        return root["available"]?.Value<bool>() ?? false;
    }

    private static DeviceInfo ParseDeviceInfo(JObject obj) => new()
    {
        DeviceId = obj["deviceId"]?.Value<string>() ?? "",
        Name = obj["name"]?.Value<string>() ?? "",
        Platform = obj["platform"]?.Value<string>() ?? "",
    };

    private static JObject EndpointToJson(Endpoint e)
    {
        var obj = new JObject
        {
            ["provider"] = e.Provider,
            ["calendarId"] = e.CalendarId,
            ["calendarName"] = e.CalendarName,
        };
        if (e.AccountRef != null) obj["accountRef"] = e.AccountRef;

        // Feature 2 source selection. Omit when unset so legacy pairs round-trip byte-identically.
        if (e.AllCalendars) obj["allCalendars"] = true;
        if (e.CalendarIds is { Count: > 0 })
            obj["calendarIds"] = new JArray(e.CalendarIds.Cast<object>().ToArray());
        if (e.CalendarNames is { Count: > 0 })
            obj["calendarNames"] = new JArray(e.CalendarNames.Cast<object>().ToArray());
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
            AllCalendars = obj["allCalendars"]?.Value<bool>() ?? false,
            CalendarIds = ParseStringArray(obj["calendarIds"]),
            CalendarNames = ParseStringArray(obj["calendarNames"]),
        };
    }

    private static IReadOnlyList<string>? ParseStringArray(JToken? token)
    {
        if (token is not JArray arr || arr.Count == 0)
            return null;
        var list = new List<string>(arr.Count);
        foreach (var item in arr)
        {
            var s = item?.Value<string>();
            if (!string.IsNullOrEmpty(s))
                list.Add(s);
        }
        return list.Count > 0 ? list : null;
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

    // Device-key transport (X-Api-Key): push + device self-management.
    private Task<JToken?> SendAsync(HttpMethod method, string path, string apiKey, JObject? body, CancellationToken ct) =>
        SendCoreAsync(method, path, req => req.Headers.Add(ApiKeyHeader, apiKey), body, ct);

    // Identity-bearer transport (Authorization: Bearer): accounts + pairs management (human-only).
    private Task<JToken?> SendBearerAsync(HttpMethod method, string path, string bearer, JObject? body, CancellationToken ct) =>
        SendCoreAsync(
            method, path,
            req => req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer),
            body, ct);

    // Identity-bearer transport that returns the RAW response body as a string (no JSON parse).
    // Used by ExportSourceTxtAsync, whose response is text/plain (.txt content), not JSON. A
    // non-2xx still throws SyncClientException, matching the JSON transport's failure contract.
    private async Task<string> SendBearerRawAsync(HttpMethod method, string path, string bearer, JObject? body, CancellationToken ct)
    {
        var url = $"{_baseUrl}{path}";
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        if (body != null)
            request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new SyncClientException(
                $"Pairs request {method} {url} failed with status {(int)response.StatusCode}: {text}");

        return text;
    }

    private async Task<JToken?> SendCoreAsync(
        HttpMethod method, string path, Action<HttpRequestMessage> applyAuth, JObject? body, CancellationToken ct)
    {
        var url = $"{_baseUrl}{path}";
        using var request = new HttpRequestMessage(method, url);
        applyAuth(request);
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
