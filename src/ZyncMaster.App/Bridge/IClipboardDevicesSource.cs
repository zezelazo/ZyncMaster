using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ZyncMaster.App.Bridge;

// Lists the signed-in user's devices for the clipboard "Devices" view. The clipboard settings ride
// the IClipboardTransport (per-device GET); this port supplies the device roster (id + name) plus
// the presence signal (LastSeenUtc) the App turns into the online flag. Kept as a narrow port so
// EngineActions can be tested with a fake and the real impl (GET /api/devices under the device api
// key) lives behind the same seam as the other server clients.
public interface IClipboardDevicesSource
{
    Task<IReadOnlyList<ClipboardDeviceRow>> ListDevicesAsync(string apiKey, CancellationToken ct = default);
}

// A device as the server knows it for the roster: its id, display name, and when it was last seen
// (its lease/presence signal). LastSeenUtc is null when the server has never seen it.
public sealed record ClipboardDeviceRow
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public DateTimeOffset? LastSeenUtc { get; init; }
}
