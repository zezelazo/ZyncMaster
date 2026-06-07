namespace ZyncMaster.Server;

// Per-device clipboard settings, scoped to the current user. GetAsync returns defaults
// (a fresh ClipboardDeviceSettings) when the device has no stored row; ListAsync returns
// every device row owned by the current user; UpsertAsync writes or updates the user's row.
public interface IClipboardSettingsStore
{
    Task<ClipboardDeviceSettings> GetAsync(string deviceId, CancellationToken ct = default);
    Task<IReadOnlyList<ClipboardDeviceSettings>> ListAsync(CancellationToken ct = default);
    Task UpsertAsync(ClipboardDeviceSettings s, CancellationToken ct = default);
}
