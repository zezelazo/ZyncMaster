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

    // Feature 2 — enumerate the LOCAL Outlook Classic (COM) calendars of this device, so the wizard
    // can offer a multi-select for an OutlookCom source. COM-only: throws when Outlook is unavailable
    // (the UI gates this behind the same OutlookCom capability that shows the COM source tile).
    // Returns the calendar display names (the "{Name} [{store}]" labels used by the source selection).
    Task<IReadOnlyList<string>> ListLocalCalendarsAsync(CancellationToken ct = default);

    // Creates a new calendar in a connected account (POST /api/accounts/{accountRef}/calendars)
    // and returns it. Used by the wizard's per-account "+ New calendar" action.
    Task<CalendarInfo> CreateCalendarAsync(string accountRef, string name, CancellationToken ct = default);

    Task<SyncPair> CreatePairAsync(string requestJson, CancellationToken ct = default);
    Task<IReadOnlyList<SyncPair>> ListPairsAsync(CancellationToken ct = default);
    Task<SyncPair> UpdatePairAsync(string requestJson, CancellationToken ct = default);
    Task DeletePairAsync(string id, CancellationToken ct = default);

    // Optional cleanup of the OLD destination after a pair is re-targeted. CleanupOldDestinationAsync
    // deletes from oldDestination ONLY the events this pair created (the server enforces that, and
    // refuses to clean the pair's current destination). CountManagedInDestinationAsync returns how
    // many such events exist, so the wizard can show "remove the N events already copied" before the
    // user opts in. Both go through the identity bearer like the rest of pair management.
    Task<ZyncMaster.Engine.CleanupResult> CleanupOldDestinationAsync(string pairId, Endpoint oldDestination, CancellationToken ct = default);
    Task<int> CountManagedInDestinationAsync(string pairId, Endpoint oldDestination, CancellationToken ct = default);
    Task<MirrorResult> RunPairNowAsync(string id, CancellationToken ct = default);

    // Track B — "Sync now" for a COM pair pinned to a DIFFERENT device. This device cannot read that
    // device's Outlook, so instead of a local run it asks the server to signal the pinned origin
    // device (POST /api/pairs/{id}/request-sync). Returns the server's status so the UI can announce
    // the outcome: "requested" (the pinned device will run it shortly), "origin_unavailable" (that
    // device is offline), "local" (the caller IS the pinned device — run locally instead), or
    // "not_com_pinned" (a non-COM pair — run it directly). The UI decides between this and
    // RunPairNowAsync by comparing the pair's pinnedDeviceId to the cached local deviceId.
    Task<ZyncMaster.Engine.RequestSyncResult> RequestPairSyncAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<string>> UnlinkAccountAsync(string accountRef, CancellationToken ct = default);

    // Auto-registers THIS device against the server using the signed-in identity bearer and
    // persists the returned api key, so the device is "paired" for every later device-key-gated
    // call (Sync now, heartbeat, getDevice, rename) WITHOUT a manual pairing step. Idempotent: a
    // no-op when a key already exists; a silent no-op when no identity is present; best-effort on
    // failure (logged, never thrown) so it can run on boot and right after sign-in without breaking
    // either flow. Returns the persisted key, or null when nothing was registered.
    Task<string?> EnsureDeviceRegisteredAsync(CancellationToken ct = default);

    // Device self-management. GetDeviceAsync reads the REAL current device (id + name + platform)
    // from the server so Settings can pre-fill the actual registered name. RenameDeviceAsync
    // renames the device in place on the server (hot rename) and returns the persisted echo.
    Task<DeviceInfo> GetDeviceAsync(CancellationToken ct = default);
    Task<DeviceInfo> RenameDeviceAsync(string name, CancellationToken ct = default);

    // Live device-name availability check for the Settings field. Returns true when the name is
    // free for this user (excluding the caller's own device), false when taken or invalid. Uses the
    // device api key.
    Task<bool> CheckDeviceNameAsync(string name, CancellationToken ct = default);

    // COM branch of the per-pair "Export .txt" flow. Writes a Simple-mode .txt export of the
    // LOCAL Outlook Classic (COM) calendar to a user-chosen path via CalExport.exe, and returns
    // the saved path or null if the save dialog was cancelled. This path is for OutlookCom
    // SOURCES only — CalExport has no Graph read path. The request JSON carries the params
    // CalExport Simple mode supports: {year, month, includeCancelled, calendarNames?}; the
    // optional calendarNames filters which COM calendar(s) by display name. For a MicrosoftGraph
    // source the App uses ExportSourceTxtAsync instead (the server reads the online calendar).
    Task<string?> GenerateTxtAsync(string requestJson, CancellationToken ct = default);

    // Graph branch of the per-pair "Export .txt" flow. The pair's SOURCE calendar lives online
    // and only the server can read it, so the .txt is built server-side; the App just saves it.
    // Asks the server for the Simple-mode .txt of the pair's source for {year, month,
    // includeCancelled}, prompts the save dialog, writes the file, and returns the saved path
    // (null if cancelled). The request JSON is {pairId, year, month, includeCancelled}.
    Task<string?> ExportSourceTxtAsync(string requestJson, CancellationToken ct = default);

    // Device capabilities, queried once at boot so the UI can gate COM-only affordances. On the
    // desktop App this probes Outlook Classic; the web panel reports everything off.
    Task<AppCapabilities> GetCapabilitiesAsync(CancellationToken ct = default);

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
    //   UpgradeAccountScopeAsync(accountId) — re-run the interactive consent to grant read/write on an
    //     already-connected (read-only) account, so it can become a sync destination. Same
    //     browser+loopback flow as ConnectCalendarAsync; reports Connected when the grant completes.
    Task<ConnectCalendarOutcome> UpgradeAccountScopeAsync(string accountId, CancellationToken ct = default);
    Task<IReadOnlyList<CalendarAccountSummary>> ListCalendarAccountsAsync(CancellationToken ct = default);

    // Opens the bundled THIRD-PARTY-NOTICES file (the open-source license notices) in the system's
    // default viewer via the shell — the same "open externally" behaviour the WebView host applies to
    // http/https links. Config-independent: the file ships next to the exe, so this works even when the
    // engine is unconfigured. Best-effort: a missing file / no viewer must not surface as an error.
    Task OpenLicensesAsync(CancellationToken ct = default);
    //   CancelConnectAsync — abort the calendar-connect attempt currently in flight (user closed the
    //     browser tab / hit Cancel) and free the loopback port so a new connectCalendar() can start
    //     right away. Mirrors CancelLoginAsync.
    Task CancelConnectAsync(CancellationToken ct = default);
}
