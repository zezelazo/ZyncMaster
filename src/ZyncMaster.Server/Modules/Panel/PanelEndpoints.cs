using Microsoft.AspNetCore.Authentication;

namespace ZyncMaster.Server;

public static class PanelEndpoints
{
    public static void MapPanelEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/panel/status", async (IConnectedAccountStore accounts, IDeviceStore devices) =>
            Results.Ok(new
            {
                connected = await accounts.HasAnyAsync(),
                deviceCount = (await devices.ListAsync()).Count,
            })).RequireCookie();

        // Current signed-in panel user. Cookie-gated; resolves the user from the ambient
        // accessor (the cookie's userId claim) and reads its profile from the user store.
        app.MapGet("/api/me", async (
            ICurrentUserAccessor currentUser,
            IUserStore users,
            CancellationToken ct) =>
        {
            var user = await users.GetAsync(currentUser.UserId, ct);
            if (user is null)
                return Results.NotFound();

            return Results.Ok(new { email = user.Email, displayName = user.DisplayName });
        }).RequireCookie();

        // Clears the panel session cookie and returns the user to the home page.
        app.MapPost("/signout", async (HttpContext context) =>
        {
            await context.SignOutAsync(AuthSchemes.Cookie);
            return Results.Redirect("/");
        });
    }
}
