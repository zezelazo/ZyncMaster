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

    // Writes a Simple-mode .txt export to a user-chosen path. Returns the saved path,
    // or null if the user cancelled the save dialog.
    Task<string?> GenerateTxtAsync(CancellationToken ct = default);

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
}
