using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ZyncMaster.App.State;
using ZyncMaster.Engine;

namespace ZyncMaster.App.Bridge;

// The set of operations the web UI can invoke on the host. UiBridge dispatches each
// inbound action to one of these; EngineActions implements them over the real sync
// engine. Kept narrow on purpose: every action maps to exactly one web verb.
public interface IEngineActions
{
    // Server warm-up probe (GET {ServerBaseUrl}/health) used at App boot to wake the Azure F1
    // free-tier server out of its cold start and confirm it is alive before the identity gate.
    // Makes ONE attempt with a short timeout; the UI owns the retry/poll loop.
    Task<ServerHealth> CheckServerHealthAsync(CancellationToken ct = default);

    Task<AppStatus> GetStatusAsync(CancellationToken ct = default);
    Task<ZyncMaster.Engine.SyncResult> SyncNowAsync(CancellationToken ct = default);
    Task<ZyncMaster.Engine.PairingOutcome> PairAsync(CancellationToken ct = default);
    Task SaveConfigAsync(string configJson, CancellationToken ct = default);
    Task SetPausedAsync(bool paused, CancellationToken ct = default);

    // Sync-pair lifecycle (WS3). Each maps to one server REST call through IPairsClient.
    Task<IReadOnlyList<AccountInfo>> ListAccountsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CalendarInfo>> ListCalendarsAsync(string accountRef, CancellationToken ct = default);

    // Creates a new calendar in a connected account (POST /api/accounts/{accountRef}/calendars)
    // and returns it. Used by the wizard's per-account "+ New calendar" action.
    Task<CalendarInfo> CreateCalendarAsync(string accountRef, string name, CancellationToken ct = default);

    Task<SyncPair> CreatePairAsync(string requestJson, CancellationToken ct = default);
    Task<IReadOnlyList<SyncPair>> ListPairsAsync(CancellationToken ct = default);
    Task<SyncPair> UpdatePairAsync(string requestJson, CancellationToken ct = default);
    Task DeletePairAsync(string id, CancellationToken ct = default);
    Task<MirrorResult> RunPairNowAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<string>> UnlinkAccountAsync(string accountRef, CancellationToken ct = default);

    // Device self-management. GetDeviceAsync reads the REAL current device (id + name + platform)
    // from the server so Settings can pre-fill the actual registered name. RenameDeviceAsync
    // renames the device in place on the server (hot rename) and returns the persisted echo.
    Task<DeviceInfo> GetDeviceAsync(CancellationToken ct = default);
    Task<DeviceInfo> RenameDeviceAsync(string name, CancellationToken ct = default);

    // Live device-name availability check for the Settings field. Returns true when the name is
    // free for this user (excluding the caller's own device), false when taken or invalid. Uses the
    // device api key.
    Task<bool> CheckDeviceNameAsync(string name, CancellationToken ct = default);

    // Writes a Simple-mode .txt export to a user-chosen path. Returns the saved path,
    // or null if the user cancelled the save dialog. The request JSON carries the export
    // parameters CalExport Simple mode supports: {year, month, includeCancelled, calendarNames?}.
    // The exported calendar is always a LOCAL Outlook Classic (COM) calendar — CalExport has no
    // Graph read path — so this only makes sense for syncs whose source is OutlookCom; the
    // optional calendarNames filters which COM calendar(s) by display name.
    Task<string?> GenerateTxtAsync(string requestJson, CancellationToken ct = default);

    // Login auto-start toggle, backed by IAutoStartManager.
    Task<bool> GetAutoStartAsync(CancellationToken ct = default);
    Task SetAutoStartAsync(bool enabled, CancellationToken ct = default);

    // Identity (sign-in) lifecycle, backed by IdentityLoginService (Phase 1 / Task 2e).
    //   GetIdentityStateAsync — current signed-in state (refreshes the token when needed).
    //   LoginAsync(provider, email)
    //     "microsoft"  → broker login (email ignored).
    //     "magic-link" → email a sign-in link to the given address (email required).
    //   SignOutAsync — clear the cached identity tokens.
    Task<IdentityState> GetIdentityStateAsync(CancellationToken ct = default);
    Task<LoginOutcome> LoginAsync(string provider, string? email, CancellationToken ct = default);
    //   CancelLoginAsync — abort the sign-in attempt currently in flight (user closed the browser
    //     tab / hit Cancel) and free the loopback port so a new login() can start right away.
    Task CancelLoginAsync(CancellationToken ct = default);
    Task SignOutAsync(CancellationToken ct = default);

    // Calendar-account connection lifecycle, backed by CalendarConnectService. Reuses the signed-in
    // identity (the IdentityBearer) to connect a Microsoft Graph calendar in one click via the same
    // system-browser + loopback pattern as sign-in.
    //   ConnectCalendarAsync(scope) — "read" | "readwrite"; defaults to read/write server-side.
    //     Opens the browser, awaits the loopback callback, verifies the nonce, and reports Connected.
    //   ListCalendarAccountsAsync — the signed-in user's connected calendar accounts (IdentityBearer).
    Task<ConnectCalendarOutcome> ConnectCalendarAsync(string scope, CancellationToken ct = default);
    Task<IReadOnlyList<CalendarAccountSummary>> ListCalendarAccountsAsync(CancellationToken ct = default);
}
