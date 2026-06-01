using FluentValidation;

namespace ZyncMaster.Server;

public static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/api/pair/start", async (PairStartRequest req, DeviceService service) =>
        {
            var validation = new PairStartRequestValidator().Validate(req);
            if (!validation.IsValid)
                return Results.ValidationProblem(validation.ToDictionary());

            var result = await service.StartPairingAsync(req.Name);
            return Results.Ok(result);
        });

        app.MapPost("/api/pair/complete", async (PairCompleteRequest req, DeviceService service) =>
        {
            var validation = new PairCompleteRequestValidator().Validate(req);
            if (!validation.IsValid)
                return Results.ValidationProblem(validation.ToDictionary());

            var result = await service.CompletePairingAsync(req.PairingId);
            return Results.Ok(result);
        });

        app.MapPost("/api/devices/approve", async (ApproveRequest req, DeviceService service) =>
        {
            var validation = new ApproveRequestValidator().Validate(req);
            if (!validation.IsValid)
                return Results.ValidationProblem(validation.ToDictionary());

            var approved = await service.ApproveAsync(req.Code);
            return Results.Ok(new { approved });
        }).RequireCookie();

        // §A-2 — brokered registration. The owner is the identity-bearer token's user (read from
        // the principal by the user-scoped DeviceService), NEVER a userId from the body. A body
        // that smuggles someone else's userId is simply ignored because the request type has no
        // such field and the service binds to the token's user.
        app.MapPost("/api/devices/register", async (
            DeviceRegisterRequest req, DeviceService service, CancellationToken ct) =>
        {
            var validation = new DeviceRegisterRequestValidator().Validate(req);
            if (!validation.IsValid)
                return Results.ValidationProblem(validation.ToDictionary());

            var result = await service.RegisterAsync(req, ct);
            return Results.Ok(new
            {
                deviceId = result.DeviceId,
                apiKey = result.ApiKey,
                leaseUntil = result.LeaseUntil,
            });
        }).RequireIdentityBearer();

        // Heartbeat — the App calls this periodically (well within DeviceLeaseTtlMinutes) to renew
        // its lease. Authenticated by the device's api key; the deviceId comes from the principal,
        // not the body, so a device can only ever renew its OWN lease.
        app.MapPost("/api/devices/heartbeat", async (
            HttpContext http, DeviceService service, CancellationToken ct) =>
        {
            var deviceId = http.User.FindFirst("deviceId")?.Value;
            if (string.IsNullOrWhiteSpace(deviceId))
                return Results.Unauthorized();

            var result = await service.HeartbeatAsync(deviceId, ct);
            if (result is null)
                return Results.Unauthorized();

            return Results.Ok(new { leaseUntil = result.LeaseUntil });
        }).RequireApiKey();

        app.MapGet("/api/devices", async (IDeviceStore store) =>
        {
            var devices = await store.ListAsync();
            return Results.Ok(devices.Select(d => new
            {
                id = d.Id,
                name = d.Name,
                targetCalendarId = d.TargetCalendarId,
                createdUtc = d.CreatedUtc,
                lastSeenUtc = d.LastSeenUtc,
            }));
        }).RequireApiKey();
    }
}
