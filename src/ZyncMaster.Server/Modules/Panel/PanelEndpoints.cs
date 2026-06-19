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
            HttpContext context,
            ICurrentUserAccessor currentUser,
            IUserStore users,
            CancellationToken ct) =>
        {
            var user = await users.GetAsync(currentUser.UserId, ct);
            if (user is null)
            {
                // The cookie is valid but its user row no longer exists (deleted out from
                // under an active session). Sign out the stale cookie and return 401 so the
                // panel cleanly shows the sign-in gate instead of a confusing 404, and the
                // dead cookie is cleared rather than re-presented on every request.
                await context.SignOutAsync(AuthSchemes.Cookie);
                return Results.Unauthorized();
            }

            return Results.Ok(new { email = user.Email, displayName = user.DisplayName });
        }).RequireCookie();

        // Clears the panel session cookie and returns the user to the home page.
        app.MapPost("/signout", async (HttpContext context) =>
        {
            await context.SignOutAsync(AuthSchemes.Cookie);
            return Results.Redirect("/");
        });

        // GDPR right-to-be-forgotten. Cookie-gated; hard-deletes the signed-in user and EVERY row
        // scoped to them (accounts, devices, pairs, sync state, clipboard, replica/prefix rules,
        // identity logins + tokens, magic links), then clears the session cookie so the panel drops
        // to the sign-in gate. Idempotent — a stale cookie whose user is already gone still 204s.
        app.MapDelete("/api/account", async (
            HttpContext context,
            ICurrentUserAccessor currentUser,
            IUserStore users,
            CancellationToken ct) =>
        {
            await users.DeleteUserAsync(currentUser.UserId, ct);
            await context.SignOutAsync(AuthSchemes.Cookie);
            return Results.NoContent();
        }).RequireCookie();
    }
}
