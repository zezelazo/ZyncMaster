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

    public Task<SyncPair> CreatePairAsync(string requestJson, CancellationToken ct = default)
        => throw NotConfigured();

    public Task<IReadOnlyList<SyncPair>> ListPairsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SyncPair>>(new List<SyncPair>());

    public Task<SyncPair> UpdatePairAsync(string requestJson, CancellationToken ct = default)
        => throw NotConfigured();

    public Task DeletePairAsync(string id, CancellationToken ct = default)
        => throw NotConfigured();

    public Task<MirrorResult> RunPairNowAsync(string id, CancellationToken ct = default)
        => throw NotConfigured();

    public Task<IReadOnlyList<string>> UnlinkAccountAsync(string accountRef, CancellationToken ct = default)
        => throw NotConfigured();

    // The basic .txt export does not need the server, but it does need a configured
    // CalExport path; without a valid engine config we cannot run it, so report cancelled.
    public Task<string?> GenerateTxtAsync(CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task<bool> GetAutoStartAsync(CancellationToken ct = default) => Task.FromResult(false);

    public Task SetAutoStartAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;

    private static InvalidOperationException NotConfigured()
        => new("Set the server URL in Settings first.");
}
