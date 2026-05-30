using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace ZyncMaster.Server;

public static class ConnectEndpoints
{
    private const string StateCookieName = "sm_oauth_state";
    private const string ReturnToCookieName = "sm_oauth_returnto";

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

            // Stash where to return after sign-in (e.g. /pair?code=...). Only same-site
            // relative paths are honored at callback time to avoid open-redirects.
            var returnTo = context.Request.Query["returnTo"].ToString();
            if (!string.IsNullOrEmpty(returnTo))
            {
                context.Response.Cookies.Append(ReturnToCookieName, returnTo, new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Secure = context.Request.IsHttps,
                });
            }

            var authorizeUrl =
                $"{options.Authority.TrimEnd('/')}/authorize" +
                $"?client_id={Uri.EscapeDataString(options.MicrosoftClientId)}" +
                "&response_type=code" +
                $"&redirect_uri={Uri.EscapeDataString(options.RedirectUri)}" +
                "&response_mode=query" +
                $"&scope={Uri.EscapeDataString(options.Scopes)}" +
                // Force the account picker on every sign-in. Without this Entra silently reuses
                // the first signed-in session, so users with several Microsoft accounts can never
                // choose which one to connect.
                "&prompt=select_account" +
                $"&state={Uri.EscapeDataString(state)}";

            return Results.Redirect(authorizeUrl);
        });

        app.MapGet("/connect/callback", async (
            HttpContext context,
            IMicrosoftTokenService tokenService,
            IConnectedAccountStore store,
            IUserStore users) =>
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

            // Upsert the ZyncMaster user from the id_token identity. Fall back to the UPN
            // for subject when the provider omitted oid/sub so we always have a stable key.
            var subject = string.IsNullOrEmpty(result.Subject)
                ? (result.UserPrincipalName ?? "")
                : result.Subject;
            var email = result.Email ?? result.UserPrincipalName ?? "";
            var displayName = result.DisplayName ?? email;
            var user = await users.UpsertAsync("microsoft", subject, email, displayName, context.RequestAborted);

            // The cookie issued below is not active within THIS request, so the ambient
            // ICurrentUserAccessor would still resolve "default". Pin the just-created user
            // for the rest of this request AND write the connected account explicitly under
            // that user id — never relying on the ambient default.
            context.Items[HttpContextCurrentUserAccessor.OverrideItemKey] = user.Id;
            await store.SetForUserAsync(
                user.Id, result.UserPrincipalName ?? "", result.RefreshToken, context.RequestAborted);

            var principal = AuthSchemes.BuildCookiePrincipal(user.Id, user.Email, user.DisplayName);
            await context.SignInAsync(AuthSchemes.Cookie, principal);

            // Consume the returnTo cookie and honor only same-site relative paths.
            var returnTo = context.Request.Cookies[ReturnToCookieName];
            context.Response.Cookies.Delete(ReturnToCookieName);
            var destination = IsSafeLocalPath(returnTo) ? returnTo! : "/";

            return Results.Redirect(destination);
        });
    }

    // Accepts only single-leading-slash relative paths ("/pair?code=x"); rejects absolute
    // URLs and protocol-relative ("//host") to prevent open redirects.
    private static bool IsSafeLocalPath(string? path) =>
        !string.IsNullOrEmpty(path) &&
        path.StartsWith('/') &&
        !path.StartsWith("//", StringComparison.Ordinal) &&
        !path.StartsWith("/\\", StringComparison.Ordinal);
}
