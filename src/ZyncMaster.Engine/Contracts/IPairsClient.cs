using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ZyncMaster.Core;

namespace ZyncMaster.Engine;

// Talks to the server's pairs/accounts REST surface.
//
// The ACCOUNTS and PAIRS management calls are HUMAN-only on the server (RequireCookieOrIdentityBearer):
// they are authenticated with the signed-in user's IDENTITY BEARER access token (sent as
// Authorization: Bearer), never the device api key. Their first argument is named `bearer`.
//
// The DEVICE self-management calls (GetDeviceMe / RenameDevice) and PushPair stay on the device
// API KEY (X-Api-Key): the server gates them with RequireApiKey / RequireCookieOrApiKeyOrIdentityBearer
// and the device background scheduler drives the push under its key. Their first argument is named
// `apiKey`.
public interface IPairsClient
{
    Task<IReadOnlyList<AccountInfo>> ListAccountsAsync(string bearer, CancellationToken ct);

    Task<IReadOnlyList<CalendarInfo>> ListCalendarsAsync(string bearer, string accountRef, CancellationToken ct);

    // Creates a new calendar in the given account and returns it. Human-only management
    // surface: authenticated with the identity bearer, like the other accounts/pairs calls.
    Task<CalendarInfo> CreateCalendarAsync(string bearer, string accountRef, string name, CancellationToken ct);

    Task<SyncPair> CreatePairAsync(string bearer, string name, Endpoint source, Endpoint destination, int intervalMin, CancellationToken ct);

    Task<IReadOnlyList<SyncPair>> ListPairsAsync(string bearer, CancellationToken ct);

    Task<SyncPair> UpdatePairAsync(string bearer, string id, string? name, int? intervalMin, string? state, CancellationToken ct, Endpoint? source = null, Endpoint? destination = null);

    // Exports the pair's SOURCE calendar for one month as a Simple-mode .txt (Graph sources
    // only; the server returns 409 no_server_reader for an OutlookCom source). The response is
    // raw text/plain — the exact .txt — not JSON, so it is returned as the crude string.
    Task<string> ExportSourceTxtAsync(string bearer, string id, int year, int month, bool includeCancelled, CancellationToken ct);

    Task DeletePairAsync(string bearer, string id, CancellationToken ct);

    Task<IReadOnlyList<string>> UnlinkAccountAsync(string bearer, string accountRef, CancellationToken ct);

    // Sync data path. Driven by the device background scheduler under the device API KEY; the
    // server also tolerates a human cookie/bearer on it (RequireCookieOrApiKeyOrIdentityBearer).
    Task<MirrorResult> PushPairAsync(string apiKey, string id, IReadOnlyList<AppointmentRecord> events, CancellationToken ct);

    Task<MirrorResult> RunPairAsync(string apiKey, string id, CancellationToken ct);

    // Device self-management. The server resolves the deviceId from the api key, so neither call
    // sends an id; a device can only ever read/rename ITSELF.
    Task<DeviceInfo> GetDeviceMeAsync(string apiKey, CancellationToken ct);

    Task<DeviceInfo> RenameDeviceAsync(string apiKey, string name, CancellationToken ct);

    // Live name-availability probe (GET /api/devices/name-available?name=...). The server scopes
    // the check to the caller's user and EXCLUDES the caller's own device, so re-typing the current
    // name reports available. Returns true when the name is free, false when taken or invalid.
    Task<bool> CheckDeviceNameAvailableAsync(string apiKey, string name, CancellationToken ct);
}
