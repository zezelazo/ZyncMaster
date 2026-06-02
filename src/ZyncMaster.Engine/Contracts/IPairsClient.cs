using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ZyncMaster.Core;

namespace ZyncMaster.Engine;

// Talks to the server's pairs/accounts REST surface. Every call carries the
// device api key in the X-Api-Key header.
public interface IPairsClient
{
    Task<IReadOnlyList<AccountInfo>> ListAccountsAsync(string apiKey, CancellationToken ct);

    Task<IReadOnlyList<CalendarInfo>> ListCalendarsAsync(string apiKey, string accountRef, CancellationToken ct);

    Task<SyncPair> CreatePairAsync(string apiKey, string name, Endpoint source, Endpoint destination, int intervalMin, CancellationToken ct);

    Task<IReadOnlyList<SyncPair>> ListPairsAsync(string apiKey, CancellationToken ct);

    Task<SyncPair> UpdatePairAsync(string apiKey, string id, string? name, int? intervalMin, string? state, CancellationToken ct);

    Task DeletePairAsync(string apiKey, string id, CancellationToken ct);

    Task<MirrorResult> PushPairAsync(string apiKey, string id, IReadOnlyList<AppointmentRecord> events, CancellationToken ct);

    Task<MirrorResult> RunPairAsync(string apiKey, string id, CancellationToken ct);

    Task<IReadOnlyList<string>> UnlinkAccountAsync(string apiKey, string accountRef, CancellationToken ct);

    // Device self-management. The server resolves the deviceId from the api key, so neither call
    // sends an id; a device can only ever read/rename ITSELF.
    Task<DeviceInfo> GetDeviceMeAsync(string apiKey, CancellationToken ct);

    Task<DeviceInfo> RenameDeviceAsync(string apiKey, string name, CancellationToken ct);
}
