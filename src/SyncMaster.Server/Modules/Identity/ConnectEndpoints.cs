using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace SyncMaster.Server;

public static class ConnectEndpoints
{
    private const string StateCookieName = "sm_oauth_state";

    public static void MapConnectEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/connect", (HttpContext context, IOptions<ServerOptions> opts) =>
        {
            var options = opts.Value;
            var state = ApiKeyGenerator.Generate();

            context.Response.Cookies.Append(StateCookieName, state, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                // Secure cookies will not round-trip over http (test host); gate on the
                // actual request scheme so https in production still gets Secure.
                Secure = context.Request.IsHttps,
            });

            var authorizeUrl =
                $"{options.Authority.TrimEnd('/')}/authorize" +
                $"?client_id={Uri.EscapeDataString(options.MicrosoftClientId)}" +
                "&response_type=code" +
                $"&redirect_uri={Uri.EscapeDataString(options.RedirectUri)}" +
                "&response_mode=query" +
                $"&scope={Uri.EscapeDataString(options.Scopes)}" +
                $"&state={Uri.EscapeDataString(state)}";

            return Results.Redirect(authorizeUrl);
        });

        app.MapGet("/connect/callback", async (
            HttpContext context,
            IMicrosoftTokenService tokenService,
            IConnectedAccountStore store) =>
        {
            var query = context.Request.Query;

            var error = query["error"].ToString();
            if (!string.IsNullOrEmpty(error))
            {
                var description = query["error_description"].ToString();
                var html =
                    "<!DOCTYPE html><html><body>" +
                    "<h1>Connection failed</h1>" +
                    $"<p>{System.Net.WebUtility.HtmlEncode(error)}</p>" +
                    (string.IsNullOrEmpty(description)
                        ? ""
                        : $"<p>{System.Net.WebUtility.HtmlEncode(description)}</p>") +
                    "</body></html>";
                return Results.Content(html, "text/html");
            }

            var state = query["state"].ToString();
            var cookieState = context.Request.Cookies[StateCookieName];
            if (string.IsNullOrEmpty(state) ||
                string.IsNullOrEmpty(cookieState) ||
                !string.Equals(state, cookieState, StringComparison.Ordinal))
            {
                return Results.BadRequest("Invalid OAuth state.");
            }

            var code = query["code"].ToString();
            if (string.IsNullOrEmpty(code))
                return Results.BadRequest("Missing authorization code.");

            var result = await tokenService.ExchangeCodeAsync(code);
            await store.SetAsync(result.UserPrincipalName ?? "", result.RefreshToken);

            return Results.Redirect("/");
        });
    }
}
