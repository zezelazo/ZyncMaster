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
        });

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
        }).RequireAuthorization();
    }
}
