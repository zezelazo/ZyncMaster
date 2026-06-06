using System;
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

    // pinnedDeviceId (Track B): when the source is OutlookCom, the App passes its own deviceId up
    // front so the pair is pinned to this machine at creation time and the dashboard shows "Source is
    // on this PC" immediately, without waiting for the first push to claim it. Null/blank for non-COM
    // pairs or when the deviceId is not yet known; the server ignores a pin on a non-COM pair.
    Task<SyncPair> CreatePairAsync(string bearer, string name, Endpoint source, Endpoint destination, int intervalMin, CancellationToken ct, string? pinnedDeviceId = null);

    Task<IReadOnlyList<SyncPair>> ListPairsAsync(string bearer, CancellationToken ct);

    Task<SyncPair> UpdatePairAsync(string bearer, string id, string? name, int? intervalMin, string? state, CancellationToken ct, Endpoint? source = null, Endpoint? destination = null);

    // Exports the pair's SOURCE calendar for one month as a Simple-mode .txt (Graph sources
    // only; the server returns 409 no_server_reader for an OutlookCom source). The response is
    // raw text/plain — the exact .txt — not JSON, so it is returned as the crude string.
    Task<string> ExportSourceTxtAsync(string bearer, string id, int year, int month, bool includeCancelled, CancellationToken ct);

    // Deletes from the pair's PREVIOUS destination only the events this pair created
    // (POST /api/pairs/{id}/cleanup-destination). Human-only management surface (identity bearer).
    // The server refuses to clean the pair's current destination and is user-scoped (404 cross-user).
    Task<CleanupResult> CleanupDestinationAsync(string bearer, string id, Endpoint oldDestination, CancellationToken ct);

    // Counts (without deleting) the events the pair created in the given destination
    // (GET /api/pairs/{id}/managed-count). Drives the wizard's cleanup-confirm count.
    Task<int> CountManagedAsync(string bearer, string id, Endpoint destination, CancellationToken ct);

    Task DeletePairAsync(string bearer, string id, CancellationToken ct);

    Task<IReadOnlyList<string>> UnlinkAccountAsync(string bearer, string accountRef, CancellationToken ct);

    // Sync data path. Driven by the device background scheduler under the device API KEY; the
    // server also tolerates a human cookie/bearer on it (RequireCookieOrApiKeyOrIdentityBearer).
    Task<MirrorResult> PushPairAsync(string apiKey, string id, IReadOnlyList<AppointmentRecord> events, CancellationToken ct);

    Task<MirrorResult> RunPairAsync(string apiKey, string id, CancellationToken ct);

    // Asks the server to sync a COM-pinned pair now (POST /api/pairs/{id}/request-sync). Used by the
    // App's "Sync now" when the caller is NOT the pinned device: instead of reading COM locally (which
    // would fail or compete), it signals the pinned device to run. Tolerates a human cookie/bearer or
    // a device api key (RequireCookieOrApiKeyOrIdentityBearer), so the first argument is a bearer-or-key.
    // Returns the server's status ("requested" / "origin_unavailable" / "local") plus the device name.
    Task<RequestSyncResult> RequestPairSyncAsync(string bearerOrKey, string id, CancellationToken ct);

    // Renews the calling device's lease (POST /api/devices/heartbeat). The App calls this on a
    // PeriodicTimer well inside DeviceLeaseTtlMinutes so the server's cron fallback treats the
    // device as "App running" and skips the user's pairs. The deviceId is resolved from the api
    // key, so the body is empty. Returns the new LeaseUntil reported by the server.
    Task<DateTimeOffset?> HeartbeatAsync(string apiKey, CancellationToken ct);

    // Brokered device registration (POST /api/devices/register). HUMAN-only surface: authenticated
    // with the signed-in user's IDENTITY BEARER (the server gates it RequireIdentityBearer and binds
    // the new device to the token's user). The body carries only the device name; the server returns
    // a fresh one-time api key + deviceId + the initial lease. The App calls this once, right after
    // sign-in, so the device has a key for every later device-key-gated call.
    Task<DeviceRegistration> RegisterDeviceAsync(string bearer, string deviceName, CancellationToken ct);

    // Device self-management. The server resolves the deviceId from the api key, so neither call
    // sends an id; a device can only ever read/rename ITSELF.
    Task<DeviceInfo> GetDeviceMeAsync(string apiKey, CancellationToken ct);

    Task<DeviceInfo> RenameDeviceAsync(string apiKey, string name, CancellationToken ct);

    // Live name-availability probe (GET /api/devices/name-available?name=...). The server scopes
    // the check to the caller's user and EXCLUDES the caller's own device, so re-typing the current
    // name reports available. Returns true when the name is free, false when taken or invalid.
    Task<bool> CheckDeviceNameAvailableAsync(string apiKey, string name, CancellationToken ct);
}
