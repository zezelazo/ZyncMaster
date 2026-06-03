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

        // Self-read — the App calls this to pre-load the REAL current name of the calling device
        // into Settings. Authenticated by the device's api key; the deviceId comes from the
        // principal, so it can only ever return the caller's OWN device.
        app.MapGet("/api/devices/me", async (
            HttpContext http, DeviceService service, CancellationToken ct) =>
        {
            var deviceId = http.User.FindFirst("deviceId")?.Value;
            if (string.IsNullOrWhiteSpace(deviceId))
                return Results.Unauthorized();

            var device = await service.GetMeAsync(deviceId, ct);
            if (device is null)
                return Results.NotFound();

            return Results.Ok(new
            {
                deviceId = device.Id,
                name = device.Name,
                platform = device.Platform,
                hasOutlookCom = device.HasOutlookCom,
                appVersion = device.AppVersion,
                createdUtc = device.CreatedUtc,
                lastSeenUtc = device.LastSeenUtc,
            });
        }).RequireApiKey();

        // Self-rename — the App calls this to rename the calling device in place. Authenticated by
        // the device's api key; the target deviceId comes from the principal, NEVER from the body,
        // so a device can only ever rename ITSELF (and only within the caller's own user scope). A
        // body that smuggles another device's id is ignored — the request type has no id field.
        app.MapPost("/api/devices/rename", async (
            DeviceRenameRequest req, HttpContext http, DeviceService service, CancellationToken ct) =>
        {
            var deviceId = http.User.FindFirst("deviceId")?.Value;
            if (string.IsNullOrWhiteSpace(deviceId))
                return Results.Unauthorized();

            var validation = new DeviceRenameRequestValidator().Validate(req);
            if (!validation.IsValid)
                return Results.ValidationProblem(validation.ToDictionary());

            DeviceRenameResult? result;
            try
            {
                result = await service.RenameAsync(deviceId, req.Name, ct);
            }
            catch (DeviceNameTakenException)
            {
                // 409 with a stable error code so the UI can show an inline "Name already used"
                // message and keep the user on the field instead of treating it as a save success.
                return Results.Conflict(new
                {
                    error = "name_taken",
                    message = "That device name is already used by another of your devices.",
                });
            }

            if (result is null)
                return Results.NotFound();

            return Results.Ok(new { deviceId = result.DeviceId, name = result.Name });
        }).RequireApiKey();

        // Live name-availability probe for the Settings "Device name" field. Authenticated by the
        // device's api key; user-scoped through the store, and the CALLER's own device is excluded
        // so re-typing the current name reports available. Returns { available: bool } for a valid
        // name, or { available:false, reason:"invalid" } for a blank / over-long one.
        app.MapGet("/api/devices/name-available", async (
            string? name, HttpContext http, DeviceService service, CancellationToken ct) =>
        {
            var deviceId = http.User.FindFirst("deviceId")?.Value;
            if (string.IsNullOrWhiteSpace(deviceId))
                return Results.Unauthorized();

            var trimmed = (name ?? string.Empty).Trim();
            if (trimmed.Length == 0 || trimmed.Length > DeviceNameGenerator.MaxNameLength)
                return Results.Ok(new { available = false, reason = "invalid" });

            var available = await service.IsNameAvailableAsync(trimmed, excludeDeviceId: deviceId, ct);
            return Results.Ok(new { available });
        }).RequireApiKey();
    }
}
