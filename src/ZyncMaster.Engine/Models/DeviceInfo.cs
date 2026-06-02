namespace ZyncMaster.Engine;

// The calling device as the server knows it (GET /api/devices/me) and the echo returned by a
// rename (POST /api/devices/rename). The App uses DeviceId + Name to pre-load and update the
// Settings "Device name" field against the real registered device.
public sealed record DeviceInfo
{
    public string DeviceId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Platform { get; init; } = "";
}
