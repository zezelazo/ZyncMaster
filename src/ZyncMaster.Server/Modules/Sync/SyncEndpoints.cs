namespace ZyncMaster.Server;

public static class SyncEndpoints
{
    public static void MapSyncEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/sync/calendar", async (SyncRequest req, HttpContext http, SyncService service, CancellationToken ct) =>
        {
            var deviceId = http.User.FindFirst("deviceId")?.Value;
            if (string.IsNullOrWhiteSpace(deviceId))
                return Results.Unauthorized();

            var outcome = await service.SyncAsync(deviceId, req.Events, ct);
            if (outcome.NoAccount)
            {
                return Results.Conflict(new
                {
                    error = "no_connected_account",
                    message = "Connect a Microsoft account in the panel first.",
                });
            }

            return Results.Ok(outcome.Response);
        }).RequireAuthorization();
    }
}
