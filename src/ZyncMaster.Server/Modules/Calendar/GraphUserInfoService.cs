using System.Net.Http.Headers;
using System.Text.Json;

namespace ZyncMaster.Server;

// Graph /me implementation of IGraphUserInfoService. Reuses the named "graph" HttpClient (the same
// pool the calendar reader/writer use) so no new HTTP dependency is introduced. Every failure mode
// — non-2xx, empty/non-JSON body, transport exception — degrades to GraphUserInfo.Empty so the
// caller (connect callback / account listing backfill) is never broken by a /me hiccup.
public sealed class GraphUserInfoService : IGraphUserInfoService
{
    private const string MeUrl =
        "https://graph.microsoft.com/v1.0/me?$select=mail,userPrincipalName,displayName";

    private readonly HttpClient _http;

    public GraphUserInfoService(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<GraphUserInfo> GetMeAsync(string accessToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            return GraphUserInfo.Empty;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, MeUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return GraphUserInfo.Empty;

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
                return GraphUserInfo.Empty;

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return GraphUserInfo.Empty;

            var mail = GetString(root, "mail");
            var upn = GetString(root, "userPrincipalName");
            var displayName = GetString(root, "displayName") ?? "";

            var email = !string.IsNullOrWhiteSpace(mail) ? mail! : (upn ?? "");
            return new GraphUserInfo(email.Trim(), displayName.Trim());
        }
        catch (HttpRequestException)
        {
            return GraphUserInfo.Empty;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // A /me timeout (not a caller cancellation) is best-effort: fall back to empty.
            return GraphUserInfo.Empty;
        }
        catch (JsonException)
        {
            return GraphUserInfo.Empty;
        }
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
}
