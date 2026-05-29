using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ZyncMaster.Engine;

public sealed class HttpPairingClient : IPairingClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public HttpPairingClient(HttpClient http, string serverBaseUrl)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        if (serverBaseUrl == null) throw new ArgumentNullException(nameof(serverBaseUrl));
        _baseUrl = serverBaseUrl.TrimEnd('/');
    }

    public async Task<PairStart> StartAsync(string deviceName, CancellationToken ct)
    {
        if (deviceName == null) throw new ArgumentNullException(nameof(deviceName));

        var body = new JObject { ["name"] = deviceName };
        var root = await PostJsonAsync($"{_baseUrl}/api/pair/start", body, ct);

        return new PairStart
        {
            PairingId = root["pairingId"]?.Value<string>() ?? "",
            Code = root["code"]?.Value<string>() ?? "",
        };
    }

    public async Task<PairComplete> CompleteAsync(string pairingId, CancellationToken ct)
    {
        if (pairingId == null) throw new ArgumentNullException(nameof(pairingId));

        var body = new JObject { ["pairingId"] = pairingId };
        var root = await PostJsonAsync($"{_baseUrl}/api/pair/complete", body, ct);

        return new PairComplete
        {
            Approved = root["approved"]?.Value<bool>() ?? false,
            ApiKey = root["apiKey"]?.Value<string>(),
            DeviceId = root["deviceId"]?.Value<string>(),
        };
    }

    private async Task<JObject> PostJsonAsync(string url, JObject body, CancellationToken ct)
    {
        using var content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(url, content, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new SyncClientException(
                $"Pairing request to {url} failed with status {(int)response.StatusCode}: {text}");

        return string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
    }
}
