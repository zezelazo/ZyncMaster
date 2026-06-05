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

            // Validate the body before touching the service. A null Events (e.g. {"events":null} or
            // a missing property) is a malformed request, not a server fault: return a stable 400
            // bad_request instead of letting ArgumentNullException.ThrowIfNull bubble up as a 500.
            if (req is null || req.Events is null)
            {
                return Results.BadRequest(new
                {
                    error = "bad_request",
                    message = "The request body must include a non-null 'events' array.",
                });
            }

            var outcome = await service.SyncAsync(deviceId, req.Events, ct);
            if (outcome.NoAccount)
            {
                return Results.Conflict(new
                {
                    error = "no_connected_account",
                    message = "Connect a Microsoft account in the panel first.",
                });
            }

            // The connected account has no calendars at all (a brand-new mailbox, or a transient
            // empty enumeration). There is nothing to mirror into, so this is a conflicting state
            // the client must resolve, not a server fault: 409 no_calendar instead of a 500 from
            // cals.First() on an empty list.
            if (outcome.NoCalendar)
            {
                return Results.Conflict(new
                {
                    error = "no_calendar",
                    message = "The connected account has no calendar to sync into.",
                });
            }

            return Results.Ok(outcome.Response);
        }).RequireApiKey();
    }
}
