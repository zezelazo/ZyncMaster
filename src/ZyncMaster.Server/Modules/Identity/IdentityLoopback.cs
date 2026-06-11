using System.Text.Json;
using ZyncMaster.Server.Data;

namespace ZyncMaster.Server;

// Shared tail of every identity sign-in flow (Microsoft OAuth in Task 2b, magic-link in Task 2c):
// mint an access+refresh token pair for the resolved user, wrap both behind a single one-time
// loopback handle, and build the http://127.0.0.1:{port}/identity/callback?handle=...&nonce=...
// redirect the desktop App listens for. Extracted so both flows emit identical token+handle+
// redirect semantics from one place.
internal static class IdentityLoopback
{
    // Issues the token pair, wraps it behind a one-time handle, and returns the loopback redirect
    // URL. The calendar refresh token (when any) is NOT involved here — identity flows prove who
    // the user is, they do not connect a calendar.
    public static async Task<string> IssueLoopbackRedirectAsync(
        UserRow user,
        int port,
        string nonce,
        IIdentityTokenService identityTokens,
        IIdentityHandleStore handles,
        CancellationToken ct)
    {
        var access = identityTokens.IssueAccessToken(user);
        var refresh = await identityTokens.IssueRefreshTokenAsync(user.Id, ct);

        var bundle = JsonSerializer.Serialize(new HandleBundle
        {
            AccessToken = access.Token,
            RefreshToken = refresh,
        });
        var handle = handles.IssueHandle(bundle);

        return
            $"http://127.0.0.1:{port}/identity/callback" +
            $"?handle={Uri.EscapeDataString(handle)}" +
            $"&nonce={Uri.EscapeDataString(nonce)}";
    }

    // Web-mode variant: same session minting + one-time handle, but the redirect target is the
    // Angular SPA's fixed origin-relative callback path (served by nginx at /zync-web/). The
    // path is a constant on purpose — accepting a caller-supplied redirect would be an open
    // redirect on an endpoint that carries a credential handle.
    public const string WebCallbackPath = "/zync-web/auth/callback";

    public static async Task<string> IssueWebRedirectAsync(
        UserRow user,
        string nonce,
        IIdentityTokenService identityTokens,
        IIdentityHandleStore handles,
        CancellationToken ct)
    {
        var access = identityTokens.IssueAccessToken(user);
        var refresh = await identityTokens.IssueRefreshTokenAsync(user.Id, ct);

        var bundle = JsonSerializer.Serialize(new HandleBundle
        {
            AccessToken = access.Token,
            RefreshToken = refresh,
        });
        var handle = handles.IssueHandle(bundle);

        return
            $"{WebCallbackPath}" +
            $"?handle={Uri.EscapeDataString(handle)}" +
            $"&nonce={Uri.EscapeDataString(nonce)}";
    }

    // The JSON wrapped behind the one-time handle and returned on /identity/handle/redeem.
    public sealed class HandleBundle
    {
        [System.Text.Json.Serialization.JsonPropertyName("accessToken")]
        public string AccessToken { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("refreshToken")]
        public string RefreshToken { get; set; } = "";
    }
}
