using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using ZyncMaster.App.Configuration;
using ZyncMaster.App.State;
using ZyncMaster.Core;
using ZyncMaster.Engine;

namespace ZyncMaster.App.Bridge;

// Engine actions used when the live engine can't be built yet (e.g. the server URL has
// not been configured). The bridge is still fully wired with this, so window controls,
// status and saving the config all keep working — the user can configure the app from the
// UI instead of requests hanging. Sync/pair return a clear "configure first" result.
public sealed class UnconfiguredEngineActions : IEngineActions
{
    private readonly ISettingsRepository<AppSettings> _settingsRepo;
    private readonly string _settingsPath;

    public UnconfiguredEngineActions(ISettingsRepository<AppSettings> settingsRepo, string settingsPath)
    {
        _settingsRepo = settingsRepo;
        _settingsPath = settingsPath;
    }

    // No server URL configured yet → report "unconfigured" so the UI skips the warm-up gate and
    // lets the user reach Settings (the identity gate / config screen) instead of polling forever.
    public Task<ServerHealth> CheckServerHealthAsync(CancellationToken ct = default)
        => Task.FromResult(ServerHealth.Unconfigured);

    public Task<AppStatus> GetStatusAsync(CancellationToken ct = default)
        => Task.FromResult(new AppStatus
        {
            Status = SyncStatus.Idle,
            Paired = false,
            LastMessage = "Set the server URL in Settings to start syncing.",
        });

    public Task<SyncResult> SyncNowAsync(CancellationToken ct = default)
        => Task.FromResult(new SyncResult { Skipped = true, SkipReason = "Not configured yet — set the server URL in Settings." });

    public Task<PairingOutcome> PairAsync(CancellationToken ct = default)
        => Task.FromResult(new PairingOutcome { Success = false, Message = "Set the server URL in Settings first." });

    public Task SaveConfigAsync(string configJson, CancellationToken ct = default)
    {
        var settings = JsonConvert.DeserializeObject<AppSettings>(configJson ?? "") ?? new AppSettings();
        _settingsRepo.Save(settings, _settingsPath);
        return Task.CompletedTask;
    }

    public Task SetPausedAsync(bool paused, CancellationToken ct = default) => Task.CompletedTask;

    // Sync-pair lifecycle needs a configured + paired engine; surface a clear error so the
    // web layer shows "configure first" instead of failing opaquely.
    public Task<IReadOnlyList<AccountInfo>> ListAccountsAsync(CancellationToken ct = default)
        => throw NotConfigured();

    public Task<IReadOnlyList<CalendarInfo>> ListCalendarsAsync(string accountRef, CancellationToken ct = default)
        => throw NotConfigured();

    // No engine yet → no CalExport runner to enumerate local calendars. Report an empty list so the
    // wizard's COM source step degrades to "no calendars" rather than throwing (it is gated by the
    // OutlookCom capability, which is also off when unconfigured).
    public Task<IReadOnlyList<string>> ListLocalCalendarsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(new List<string>());

    public Task<CalendarInfo> CreateCalendarAsync(string accountRef, string name, CancellationToken ct = default)
        => throw NotConfigured();

    public Task<SyncPair> CreatePairAsync(string requestJson, CancellationToken ct = default)
        => throw NotConfigured();

    public Task<IReadOnlyList<SyncPair>> ListPairsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SyncPair>>(new List<SyncPair>());

    public Task<SyncPair> UpdatePairAsync(string requestJson, CancellationToken ct = default)
        => throw NotConfigured();

    public Task DeletePairAsync(string id, CancellationToken ct = default)
        => throw NotConfigured();

    // Destination cleanup needs a configured + paired engine; surface the clear "configure first"
    // error so the wizard's cleanup step degrades visibly rather than failing opaquely.
    public Task<CleanupResult> CleanupOldDestinationAsync(string pairId, Endpoint oldDestination, CancellationToken ct = default)
        => throw NotConfigured();

    // No engine yet → nothing to count; report 0 so the wizard's confirm shows "0 events" rather
    // than throwing (mirrors CheckDeviceNameAsync degrading quietly).
    public Task<int> CountManagedInDestinationAsync(string pairId, Endpoint oldDestination, CancellationToken ct = default)
        => Task.FromResult(0);

    public Task<MirrorResult> RunPairNowAsync(string id, CancellationToken ct = default)
        => throw NotConfigured();

    public Task<RequestSyncResult> RequestPairSyncAsync(string id, CancellationToken ct = default)
        => throw NotConfigured();

    public Task<IReadOnlyList<string>> UnlinkAccountAsync(string accountRef, CancellationToken ct = default)
        => throw NotConfigured();

    // No engine yet → no server URL to register against. A quiet no-op (null) so a boot/post-login
    // ensureDevice call degrades silently rather than throwing — the device registers once the
    // engine is configured and a sign-in happens.
    public Task<string?> EnsureDeviceRegisteredAsync(CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    // Device self-management needs a configured + paired engine; surface the clear "configure
    // first" error so the UI degrades visibly instead of failing opaquely.
    public Task<DeviceInfo> GetDeviceAsync(CancellationToken ct = default)
        => throw NotConfigured();

    public Task<DeviceInfo> RenameDeviceAsync(string name, CancellationToken ct = default)
        => throw NotConfigured();

    // No engine yet → there is no device to check against, so report "not available" rather than
    // throwing: the UI's live check degrades to a quiet ✗ instead of a noisy error.
    public Task<bool> CheckDeviceNameAsync(string name, CancellationToken ct = default)
        => Task.FromResult(false);

    // The basic .txt export does not need the server, but it does need a configured
    // CalExport path; without a valid engine config we cannot run it, so report cancelled.
    public Task<string?> GenerateTxtAsync(string requestJson, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    // Mirror GenerateTxtAsync: degrade quietly to "cancelled" (null) rather than throwing. Both
    // .txt export branches (COM via generateTxt, Graph via this one) must behave the same when the
    // engine is unconfigured, so the UI shows the clean "Save cancelled" path instead of a red error.
    public Task<string?> ExportSourceTxtAsync(string requestJson, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    // No engine yet → no device probe; report everything off so COM affordances stay disabled.
    public Task<AppCapabilities> GetCapabilitiesAsync(CancellationToken ct = default)
        => Task.FromResult(new AppCapabilities { OutlookCom = false });

    public Task<bool> GetAutoStartAsync(CancellationToken ct = default) => Task.FromResult(false);

    public Task SetAutoStartAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;

    // Identity stubs (§C-6): the bridge must keep answering these even before the engine is
    // configured, so the UI degrades visibly (signed-out + a "configure first" error) instead of
    // throwing "Unknown action". Signing in needs the server URL, hence the clear failure.
    public Task<IdentityState> GetIdentityStateAsync(CancellationToken ct = default)
        => Task.FromResult(IdentityState.SignedOut);

    // No engine → no identity can be in use yet, so the cheap presence check is always false. Mirrors
    // GetIdentityStateAsync's signed-out stub; the clipboard boot gate then quietly waits for sign-in.
    public Task<bool> HasIdentityAsync(CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<LoginOutcome> LoginAsync(string provider, string? email, CancellationToken ct = default)
        => Task.FromResult(LoginOutcome.Fail("Set the server URL in Settings before signing in."));

    public Task CancelLoginAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task SignOutAsync(CancellationToken ct = default) => Task.CompletedTask;

    // Calendar-connect stubs: connecting needs the server URL + a signed-in identity, neither of
    // which exists yet, so report the clear "configure first" failure and an empty account list.
    public Task<ConnectCalendarOutcome> ConnectCalendarAsync(string scope, CancellationToken ct = default)
        => Task.FromResult(ConnectCalendarOutcome.Fail("Set the server URL in Settings before connecting a calendar."));

    public Task<ConnectCalendarOutcome> UpgradeAccountScopeAsync(string accountId, CancellationToken ct = default)
        => Task.FromResult(ConnectCalendarOutcome.Fail("Set the server URL in Settings before connecting a calendar."));

    public Task<IReadOnlyList<CalendarAccountSummary>> ListCalendarAccountsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<CalendarAccountSummary>>(new List<CalendarAccountSummary>());

    // No connect can be in flight without a configured engine, so cancelling is a quiet no-op
    // (mirrors CancelLoginAsync).
    public Task CancelConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    // Opening the open-source notices is config-independent (the file ships next to the exe), so it
    // works even before the server URL is set. Best-effort, like the live engine's implementation.
    public Task OpenLicensesAsync(CancellationToken ct = default)
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
            if (System.IO.File.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { /* no viewer / blocked: swallow */ }
        return Task.CompletedTask;
    }

    // ---------------- Calendar v2 ----------------
    // Day view / replicas / rules need a configured server: clear "configure first" error.
    // The rules LIST degrades to an empty array so the panel renders its zero state quietly.
    public Task<string> GetCalendarDayAsync(string dateIso, CancellationToken ct = default)
        => throw NotConfigured();

    public Task<string> CreateCalendarEventAsync(string requestJson, CancellationToken ct = default)
        => throw NotConfigured();

    public Task<string> CreateEventReplicasAsync(string requestJson, CancellationToken ct = default)
        => throw NotConfigured();

    public Task<string> ListPrefixRulesAsync(CancellationToken ct = default)
        => Task.FromResult("[]");

    public Task<string> SavePrefixRuleAsync(string ruleJson, CancellationToken ct = default)
        => throw NotConfigured();

    public Task DeletePrefixRuleAsync(string ruleId, CancellationToken ct = default)
        => throw NotConfigured();

    // ---------------- Clipboard module (Plan 2/3) ----------------
    // No engine yet → no transport / sink / hotkey. History + devices degrade to empty so the viewer
    // shows its "nothing yet" state instead of throwing; the mutating actions degrade to a quiet
    // no-op (the user has not configured the server, so there is nothing to persist or paste).
    public Task<IReadOnlyList<ClipboardHistoryItem>> GetClipboardHistoryAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ClipboardHistoryItem>>(new List<ClipboardHistoryItem>());

    public Task<ClipboardDevicesView> GetClipboardDevicesAsync(CancellationToken ct = default)
    {
        // Surface the persisted paste-panel opacity even before the engine is configured so the
        // settings slider initialises correctly. Best-effort: an unreadable file falls back to 70.
        var opacity = 70;
        try
        {
            var current = _settingsRepo.TryLoad(_settingsPath);
            if (current != null)
                opacity = Math.Clamp(current.PastePanelOpacity, 0, 100);
        }
        catch { /* unreadable settings: keep the default */ }

        return Task.FromResult(new ClipboardDevicesView { PastePanelOpacity = opacity });
    }

    public Task UpdateClipboardSettingsAsync(string payloadJson, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<bool> PasteClipboardEntryAsync(string id, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<bool> CopyClipboardEntryAsync(string id, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task DeleteClipboardEntryAsync(string id, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SetClipboardHotkeyAsync(string hotkey, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task CloseClipboardViewerAsync(CancellationToken ct = default)
        => Task.CompletedTask;

    // Paste-panel opacity is an App-local UI setting persisted in settings.json — no engine needed,
    // so it works even before the server URL is configured (mirrors SaveConfigAsync writing to disk).
    public Task SetPastePanelOpacityAsync(int opacity, CancellationToken ct = default)
    {
        var clamped = Math.Clamp(opacity, 0, 100);
        var current = _settingsRepo.TryLoad(_settingsPath) ?? new AppSettings();
        current.PastePanelOpacity = clamped;
        _settingsRepo.Save(current, _settingsPath);
        return Task.CompletedTask;
    }

    private static InvalidOperationException NotConfigured()
        => new("Set the server URL in Settings first.");
}
