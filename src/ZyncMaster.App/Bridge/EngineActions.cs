using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ZyncMaster.App.Configuration;
using ZyncMaster.App.State;
using ZyncMaster.Core;
using ZyncMaster.Engine;

namespace ZyncMaster.App.Bridge;

// IEngineActions over the real sync engine. It owns the engine composition (mirroring
// the Cli's Program.cs): HttpClient -> HttpPairingClient / HttpSyncClient, the device
// key store, CalExportRunner -> CompleteCalendarReader -> OutlookComSource,
// DefaultBrowserLauncher, SystemClock, PairingService and SyncEngine.
//
// It also holds the live status the host pushes to the UI: the last SyncResult, whether
// a key is present (paired), and the user-controlled paused flag the SyncLoop honours.
public sealed class EngineActions : IEngineActions, IDisposable
{
    private readonly IDeviceKeyStore _keys;
    private readonly PairingService _pairing;
    private readonly SyncEngine _sync;
    private readonly ISettingsRepository<AppSettings> _settingsRepo;
    private readonly AppSettingsResolver _resolver;
    private readonly string _settingsPath;
    private readonly HttpClient? _ownedHttp;

    private SyncStatus _status = SyncStatus.Idle;
    private bool _paused;
    private string? _lastMessage;
    private DateTimeOffset? _lastSyncUtc;
    private SyncPushResult? _lastPush;
    private string? _lastPairingCode;

    public EngineActions(
        IDeviceKeyStore keys,
        PairingService pairing,
        SyncEngine sync,
        ISettingsRepository<AppSettings> settingsRepo,
        AppSettingsResolver resolver,
        string settingsPath,
        HttpClient? ownedHttp = null)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _pairing = pairing ?? throw new ArgumentNullException(nameof(pairing));
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));
        _settingsRepo = settingsRepo ?? throw new ArgumentNullException(nameof(settingsRepo));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _settingsPath = settingsPath ?? throw new ArgumentNullException(nameof(settingsPath));
        _ownedHttp = ownedHttp;
    }

    public bool IsPaused => _paused;

    public async Task<AppStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var key = await _keys.LoadAsync(ct);
        var paired = !string.IsNullOrEmpty(key);

        return new AppStatus
        {
            Status = _paused ? SyncStatus.Paused : _status,
            Paired = paired,
            Paused = _paused,
            PairingCode = _lastPairingCode,
            NoConnectedAccount = _lastPush?.NoConnectedAccount ?? false,
            LastMessage = _lastMessage,
            LastSyncUtc = _lastSyncUtc,
            Created = _lastPush?.Created ?? 0,
            Updated = _lastPush?.Updated ?? 0,
            Deleted = _lastPush?.Deleted ?? 0,
            Skipped = _lastPush?.Skipped ?? 0,
        };
    }

    public async Task<SyncResult> SyncNowAsync(CancellationToken ct = default)
    {
        _status = SyncStatus.Syncing;
        var result = await _sync.RunCycleAsync(ct);
        RecordResult(result);
        return result;
    }

    public async Task<PairingOutcome> PairAsync(CancellationToken ct = default)
    {
        var outcome = await _pairing.EnsurePairedAsync(ct);
        _lastPairingCode = outcome.Code;
        if (!outcome.Success)
            _status = SyncStatus.Error;
        return outcome;
    }

    public Task SaveConfigAsync(string configJson, CancellationToken ct = default)
    {
        if (configJson == null) throw new ArgumentNullException(nameof(configJson));

        var settings = Newtonsoft.Json.JsonConvert.DeserializeObject<AppSettings>(configJson)
                       ?? new AppSettings();

        // Validate before persisting so a bad config never silently lands on disk.
        _ = _resolver.Resolve(settings);
        _settingsRepo.Save(settings, _settingsPath);
        return Task.CompletedTask;
    }

    public Task SetPausedAsync(bool paused, CancellationToken ct = default)
    {
        _paused = paused;
        _status = paused ? SyncStatus.Paused : SyncStatus.Idle;
        return Task.CompletedTask;
    }

    // Captures the outcome of a cycle so GetStatus / PushStatus reflect it. Called by the
    // SyncLoop wrapper as well as SyncNowAsync.
    public void RecordResult(SyncResult result)
    {
        if (result == null) return;

        if (result.Skipped)
        {
            _status = _paused ? SyncStatus.Paused : SyncStatus.Error;
            _lastMessage = result.SkipReason;
            return;
        }

        _lastPush = result.Push;
        _lastSyncUtc = DateTimeOffset.UtcNow;
        _status = _paused ? SyncStatus.Paused : SyncStatus.Idle;

        var push = result.Push;
        if (push == null)
            _lastMessage = "No result.";
        else if (push.NoConnectedAccount)
            _lastMessage = "No Microsoft account connected on the server yet.";
        else
            _lastMessage = $"created {push.Created}, updated {push.Updated}, deleted {push.Deleted}, skipped {push.Skipped}";
    }

    public void Dispose()
    {
        _ownedHttp?.Dispose();
    }
}
