namespace SyncMaster.Server;

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
            }));
    }
}
