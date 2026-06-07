using System;
using System.Collections.Generic;
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
// For the WS3 sync-pair lifecycle it also holds an IPairsClient (the server's
// pairs/accounts REST surface), a BasicTxtExporter, an IAutoStartManager, and a
// save-dialog delegate the App provides (an Avalonia save picker; a fake in tests).
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

    private readonly IPairsClient _pairs;
    private readonly IIdentityTokenCache _identityCache;
    private readonly BasicTxtExporter _txtExporter;
    private readonly IAutoStartManager _autoStart;
    private readonly EngineSettings _engineSettings;
    private readonly Func<string, Task<string?>> _saveDialog;
    private readonly string _autoStartExePath;
    private readonly IdentityLoginService _identity;
    private readonly CalendarConnectService _calendarConnect;
    private readonly IOutlookComProbe _comProbe;
    private readonly ICalendarSource _comSource;
    private readonly ICalExportRunner _calExportRunner;
    private readonly IClock _clock;
    private readonly HttpClient _http;
    private readonly IAppLogger _logger;
    private readonly string _healthUrl;

    // ---------------- Clipboard module (Plan 2/3) ----------------
    // All clipboard collaborators are OPTIONAL so the engine still builds (and every non-clipboard
    // action keeps working) on a host that does not wire the clipboard (e.g. a future non-Windows
    // build, or tests that don't exercise it). A clipboard action with no collaborators wired reports
    // a clear "not available" rather than NREing.
    private readonly IClipboardTransport? _clipboardTransport;
    private readonly IClipboardSink? _clipboardSink;
    private readonly IClipboardKeyStore? _clipboardKeys;
    private readonly IClipboardHotkey? _clipboardHotkey;
    private readonly IClipboardDevicesSource? _clipboardDevices;

    // The live, in-process clipboard settings for THIS device. The capture/apply pipeline reads this
    // through the Func<ClipboardSettings> the ClipboardService was given (App passes () =>
    // CurrentClipboardSettings), so an updateClipboardSettings for this device takes effect at once.
    public ClipboardSettings CurrentClipboardSettings { get; private set; } = new();

    // The id/name of THIS device once known (set by the App after registration). Used to mark the
    // "isThis" device in the devices view and as the origin when capturing. Empty until set.
    private string _clipboardDeviceId = "";
    private string? _clipboardDeviceName;

    // The origin identity the capture source stamps on each local copy. Read live so a registration
    // that lands after capture started still gets the right id/name on the next copy.
    public (string id, string? name) ClipboardOrigin => (_clipboardDeviceId, _clipboardDeviceName);

    // Set by the App so the engine can close the viewer window after a paste / on closeClipboardViewer.
    // A no-op when the App did not wire a viewer (headless).
    public Action? CloseClipboardViewer { get; set; }

    // Paste seam: routes a paste through the ClipboardService so the dedupe is marked BEFORE the OS
    // write, suppressing the echo capture (otherwise our own paste bounces back onto the wire as a new
    // copy). Set by the host once the ClipboardService is built (construction order means it can't be a
    // ctor arg). When unset (e.g. headless/tests that only wire a sink), PasteClipboardEntryAsync falls
    // back to the raw sink — correct, just without echo suppression. Returns whether the OS write
    // actually happened.
    public Func<ClipboardEntry, CancellationToken, Task<bool>>? PasteThroughClipboardService { get; set; }

    // Device is considered online when its last-seen lease is within this window. The server heartbeat
    // runs comfortably inside this, so a live App keeps its device "online" for the roster.
    private static readonly TimeSpan DeviceOnlineWindow = TimeSpan.FromMinutes(10);

    // One warm-up probe must fail fast, not hang on the App's default HttpClient timeout: the UI
    // re-polls, so a short per-attempt budget keeps the "waking up" feedback responsive.
    private static readonly TimeSpan HealthProbeTimeout = TimeSpan.FromSeconds(8);

    // Track B — this device's own deviceId, resolved lazily (GET /api/devices/me) and cached. Used
    // by "Sync now" to decide whether THIS device is the one pinned to a COM pair (run locally) or
    // not (signal the pinned device via request-sync). Null until first resolved.
    private string? _deviceId;

    // Serialises device registration so two concurrent callers (App boot + the UI's onServerReady +
    // refreshIdentity all fire EnsureDeviceRegisteredAsync near-simultaneously) never each register a
    // fresh device. Without this, every concurrent register re-keys the SAME server device to a
    // different key while saving its own locally — the server keeps the LAST write, the local store
    // keeps an UNRELATED one, and every later api-key call 401s. The gate + a double-checked key load
    // means exactly one register runs; the others reuse the stored key.
    private readonly SemaphoreSlim _registerGate = new(1, 1);

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
        IPairsClient pairs,
        IIdentityTokenCache identityCache,
        BasicTxtExporter txtExporter,
        IAutoStartManager autoStart,
        EngineSettings engineSettings,
        Func<string, Task<string?>> saveDialog,
        string autoStartExePath,
        IdentityLoginService identity,
        CalendarConnectService calendarConnect,
        IOutlookComProbe comProbe,
        ICalendarSource comSource,
        ICalExportRunner calExportRunner,
        IClock clock,
        HttpClient http,
        IAppLogger logger,
        HttpClient? ownedHttp = null,
        IClipboardTransport? clipboardTransport = null,
        IClipboardSink? clipboardSink = null,
        IClipboardKeyStore? clipboardKeys = null,
        IClipboardHotkey? clipboardHotkey = null,
        IClipboardDevicesSource? clipboardDevices = null)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _pairing = pairing ?? throw new ArgumentNullException(nameof(pairing));
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));
        _settingsRepo = settingsRepo ?? throw new ArgumentNullException(nameof(settingsRepo));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _settingsPath = settingsPath ?? throw new ArgumentNullException(nameof(settingsPath));
        _pairs = pairs ?? throw new ArgumentNullException(nameof(pairs));
        _identityCache = identityCache ?? throw new ArgumentNullException(nameof(identityCache));
        _txtExporter = txtExporter ?? throw new ArgumentNullException(nameof(txtExporter));
        _autoStart = autoStart ?? throw new ArgumentNullException(nameof(autoStart));
        _engineSettings = engineSettings ?? throw new ArgumentNullException(nameof(engineSettings));
        _saveDialog = saveDialog ?? throw new ArgumentNullException(nameof(saveDialog));
        _autoStartExePath = autoStartExePath ?? throw new ArgumentNullException(nameof(autoStartExePath));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _calendarConnect = calendarConnect ?? throw new ArgumentNullException(nameof(calendarConnect));
        _comProbe = comProbe ?? throw new ArgumentNullException(nameof(comProbe));
        _comSource = comSource ?? throw new ArgumentNullException(nameof(comSource));
        _calExportRunner = calExportRunner ?? throw new ArgumentNullException(nameof(calExportRunner));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _healthUrl = $"{(_engineSettings.ServerBaseUrl ?? "").TrimEnd('/')}/health";
        _ownedHttp = ownedHttp;

        // Clipboard collaborators are optional (see field comments); no null-guard here.
        _clipboardTransport = clipboardTransport;
        _clipboardSink = clipboardSink;
        _clipboardKeys = clipboardKeys;
        _clipboardHotkey = clipboardHotkey;
        _clipboardDevices = clipboardDevices;
    }

    // Seeds the live clipboard settings + this device's identity once the App has loaded them (after
    // sign-in + device registration). Called by EngineHost/App composition before the capture loop
    // starts so getClipboardDevices marks the right device and capture stamps the right origin.
    public void InitializeClipboard(ClipboardSettings settings, string deviceId, string? deviceName = null)
    {
        CurrentClipboardSettings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clipboardDeviceId = deviceId ?? "";
        _clipboardDeviceName = deviceName;
    }

    public bool IsPaused => _paused;

    // Single warm-up probe of GET {ServerBaseUrl}/health. The Azure F1 free tier cold-starts, so a
    // timeout (or a transient 5xx) almost certainly means the server is waking, not dead — that maps
    // to "waking" so the UI keeps polling. A hard transport failure (DNS/refused/offline) maps to
    // "unreachable". A 2xx is "ok". The UI owns the retry budget that covers the full cold start.
    public async Task<ServerHealth> CheckServerHealthAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_engineSettings.ServerBaseUrl))
            return ServerHealth.Unconfigured;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(HealthProbeTimeout);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _healthUrl);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);

            if (resp.IsSuccessStatusCode)
                return ServerHealth.Healthy;

            // 5xx / 429 / 408 while waking are expected during a cold start; treat anything non-2xx
            // as "still waking" so the UI re-polls rather than giving up on a transient blip.
            return ServerHealth.Waking($"Server responded with {(int)resp.StatusCode}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller (shutdown) cancelled — re-throw so the UI/host treats it as a cancellation.
            throw;
        }
        catch (OperationCanceledException)
        {
            // Our per-attempt timeout fired: the server is most likely cold-starting.
            return ServerHealth.Waking("The server did not respond in time — it may be waking up.");
        }
        catch (HttpRequestException ex)
        {
            _logger.Log(LogLevel.Warning, $"Server health check: unreachable ({_healthUrl}).", ex);
            return ServerHealth.Unreachable(ex.Message);
        }
    }

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
        _logger.Log(LogLevel.Info, "Sync now: starting cycle.");
        try
        {
            var result = await _sync.RunCycleAsync(ct);
            RecordResult(result);
            _logger.Log(LogLevel.Info, $"Sync now: finished ({_lastMessage}).");
            return result;
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Error, "Sync now: cycle failed.", ex);
            throw;
        }
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

    // ---------------- WS3: sync-pair lifecycle ----------------

    public async Task<IReadOnlyList<AccountInfo>> ListAccountsAsync(CancellationToken ct = default)
    {
        var bearer = await RequireBearerAsync(ct);
        return await _pairs.ListAccountsAsync(bearer, ct);
    }

    public async Task<IReadOnlyList<CalendarInfo>> ListCalendarsAsync(string accountRef, CancellationToken ct = default)
    {
        if (accountRef == null) throw new ArgumentNullException(nameof(accountRef));
        var bearer = await RequireBearerAsync(ct);
        return await _pairs.ListCalendarsAsync(bearer, accountRef, ct);
    }

    public async Task<IReadOnlyList<string>> ListLocalCalendarsAsync(CancellationToken ct = default)
    {
        // COM-only: enumerating local Outlook calendars requires Outlook Classic. Gate on the same
        // capability the UI uses to show the COM source tile, so a machine without Outlook gets a
        // clear error instead of a process-launch failure.
        if (!_comProbe.IsAvailable())
            throw new InvalidOperationException("Outlook Classic is not available on this device.");

        _logger.Log(LogLevel.Info, "List local calendars: enumerating Outlook Classic calendars via CalExport.");
        var names = await _calExportRunner.ListCalendarsAsync(ct);
        _logger.Log(LogLevel.Info, $"List local calendars: found {names.Count} calendar(s).");
        return names;
    }

    public async Task<CalendarInfo> CreateCalendarAsync(string accountRef, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(accountRef)) throw new ArgumentNullException(nameof(accountRef));
        if (name == null) throw new ArgumentNullException(nameof(name));
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            throw new InvalidOperationException("Calendar name is required.");

        var bearer = await RequireBearerAsync(ct);
        return await _pairs.CreateCalendarAsync(bearer, accountRef, trimmed, ct);
    }

    public async Task<SyncPair> CreatePairAsync(string requestJson, CancellationToken ct = default)
    {
        if (requestJson == null) throw new ArgumentNullException(nameof(requestJson));
        var bearer = await RequireBearerAsync(ct);

        var dto = Newtonsoft.Json.JsonConvert.DeserializeObject<CreatePairDto>(requestJson)
                  ?? throw new InvalidOperationException("Invalid create-pair request.");

        var interval = dto.IntervalMin ?? _engineSettings.IntervalMinutes;
        return await _pairs.CreatePairAsync(
            bearer,
            dto.Name ?? "",
            dto.Source?.ToEndpoint() ?? new Endpoint(),
            dto.Destination?.ToEndpoint() ?? new Endpoint(),
            interval,
            ct,
            dto.PinnedDeviceId);
    }

    public async Task<IReadOnlyList<SyncPair>> ListPairsAsync(CancellationToken ct = default)
    {
        var bearer = await RequireBearerAsync(ct);
        return await _pairs.ListPairsAsync(bearer, ct);
    }

    public async Task<SyncPair> UpdatePairAsync(string requestJson, CancellationToken ct = default)
    {
        if (requestJson == null) throw new ArgumentNullException(nameof(requestJson));
        var bearer = await RequireBearerAsync(ct);

        var dto = Newtonsoft.Json.JsonConvert.DeserializeObject<UpdatePairDto>(requestJson)
                  ?? throw new InvalidOperationException("Invalid update-pair request.");
        if (string.IsNullOrEmpty(dto.Id))
            throw new InvalidOperationException("update-pair request is missing 'id'.");

        return await _pairs.UpdatePairAsync(
            bearer, dto.Id, dto.Name, dto.IntervalMin, dto.State, ct,
            dto.Source?.ToEndpoint(), dto.Destination?.ToEndpoint());
    }

    public async Task DeletePairAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
        var bearer = await RequireBearerAsync(ct);
        await _pairs.DeletePairAsync(bearer, id, ct);
    }

    public async Task<CleanupResult> CleanupOldDestinationAsync(string pairId, Endpoint oldDestination, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(pairId)) throw new ArgumentNullException(nameof(pairId));
        if (oldDestination == null) throw new ArgumentNullException(nameof(oldDestination));
        var bearer = await RequireBearerAsync(ct);
        return await _pairs.CleanupDestinationAsync(bearer, pairId, oldDestination, ct);
    }

    public async Task<int> CountManagedInDestinationAsync(string pairId, Endpoint oldDestination, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(pairId)) throw new ArgumentNullException(nameof(pairId));
        if (oldDestination == null) throw new ArgumentNullException(nameof(oldDestination));
        var bearer = await RequireBearerAsync(ct);
        return await _pairs.CountManagedAsync(bearer, pairId, oldDestination, ct);
    }

    public async Task<MirrorResult> RunPairNowAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

        _logger.Log(LogLevel.Info, $"Sync now: requested for pair '{id}'.");

        // Fail fast if this device is not (and cannot be) registered: "Sync now" needs a device key
        // for the push, so an unpaired/unregistered device should report that up front rather than
        // after listing pairs. (The actual push below re-acquires the key through WithDeviceKeyAsync,
        // which additionally self-heals a stale key on a 401.)
        await RequireKeyAsync(ct);

        // We need the pair's Source.Provider to decide the data path: the server has no local COM
        // reader, so a COM-sourced pair must be read here (Outlook COM) and PUSHED, exactly like
        // the background PairScheduler does. Listing pairs is human-only, so it uses the bearer.
        var bearer = await RequireBearerAsync(ct);
        var pairs = await _pairs.ListPairsAsync(bearer, ct);
        SyncPair? pair = null;
        foreach (var p in pairs)
        {
            if (string.Equals(p.Id, id, StringComparison.Ordinal))
            {
                pair = p;
                break;
            }
        }

        if (pair == null)
        {
            _logger.Log(LogLevel.Warning, $"Sync now: pair '{id}' was not found.");
            throw new InvalidOperationException($"Sync pair '{id}' was not found.");
        }

        // Run is dual-scheme on the server (RequireCookieOrApiKey); the device drives it under its
        // api key, so this human "Sync now" path uses the device key for the actual push/run. The key
        // op is wrapped so a stale key (401) self-heals: clear + re-register + retry once.
        try
        {
            var result = await WithDeviceKeyAsync(async (key, c) =>
            {
                // Track B — COM device-pinning routing. A COM-sourced pair can only be READ locally on
                // the device that owns its Outlook. If THIS device is NOT the pinned one, reading COM
                // here would fail (or compete with the real origin), so the App must NOT run it
                // locally; the UI routes that case to RequestPairSyncAsync. We hard-guard here too so a
                // stray runPairNow on the wrong device fails clearly rather than reading the wrong
                // Outlook.
                if (PairRunner.IsOutlookCom(pair) && !string.IsNullOrEmpty(pair.PinnedDeviceId))
                {
                    var myDeviceId = await ResolveDeviceIdAsync(key, c);
                    if (!string.Equals(pair.PinnedDeviceId, myDeviceId, StringComparison.Ordinal))
                    {
                        _logger.Log(LogLevel.Warning,
                            $"Sync now: pair '{id}' is COM-pinned to another device; use request-sync instead of a local run.");
                        throw new InvalidOperationException(
                            "This calendar's source lives on another device. Use Sync now to ask that device to run it.");
                    }
                }

                // PairRunner is the single source of truth for the COM-vs-Graph decision and the
                // [now, now + SyncWindowDays] read window, shared with PairScheduler. For a COM pair
                // this reads Outlook locally and PUSHES; the push claims the pin on first run.
                return await PairRunner.RunOnceAsync(
                    _pairs, _comSource, pair, key, _clock.UtcNow, _engineSettings, c, _logger);
            }, ct);

            if (result.RunInProgress)
                _logger.Log(LogLevel.Info,
                    $"Sync now: pair '{id}' is already syncing (a scheduled run is in progress); this manual run was skipped.");
            else
                _logger.Log(LogLevel.Info,
                    $"Sync now: pair '{id}' done (created {result.Created}, updated {result.Updated}, deleted {result.Deleted}, skipped {result.Skipped}).");
            return result;
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Error, $"Sync now: pair '{id}' failed.", ex);
            throw;
        }
    }

    // Track B — "Sync now" for a COM pair that is NOT pinned to this device. Instead of reading COM
    // locally (which this device cannot do), it signals the pinned origin device through the server
    // (POST /api/pairs/{id}/request-sync). Returns the server's status so the UI can announce the
    // outcome ("requested" → runs on <device>, "origin_unavailable" → that device is offline,
    // "local" → caller IS the pinned device and should have run locally, "not_com_pinned" → use a
    // normal run). Authenticated with the device api key, which the endpoint accepts.
    public async Task<RequestSyncResult> RequestPairSyncAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));

        _logger.Log(LogLevel.Info, $"Request sync: signalling the pinned device for pair '{id}'.");

        try
        {
            var result = await WithDeviceKeyAsync((key, c) => _pairs.RequestPairSyncAsync(key, id, c), ct);
            _logger.Log(LogLevel.Info, $"Request sync: pair '{id}' -> status '{result.Status}' (device '{result.DeviceName}').");
            return result;
        }
        catch (SyncClientException ex) when (ex.Message.Contains("not_com_pinned", StringComparison.Ordinal))
        {
            // The server 409s a non-COM pair (it should be run, not signalled). Surface a stable
            // status the UI can branch on rather than leaking the raw transport error.
            _logger.Log(LogLevel.Warning, $"Request sync: pair '{id}' is not COM-pinned; the UI should run it directly.");
            return new RequestSyncResult { Status = "not_com_pinned" };
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Error, $"Request sync: pair '{id}' failed.", ex);
            throw;
        }
    }

    // Resolves and caches THIS device's id (GET /api/devices/me). Best-effort: a failure returns
    // null, so the caller treats "unknown id" as "not the pinned device" — the safe default that
    // routes a COM pair to request-sync rather than a wrong-device local read.
    private async Task<string?> ResolveDeviceIdAsync(string apiKey, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_deviceId))
            return _deviceId;

        try
        {
            var me = await _pairs.GetDeviceMeAsync(apiKey, ct);
            if (!string.IsNullOrEmpty(me.DeviceId))
                _deviceId = me.DeviceId;
        }
        catch (SyncClientException ex) when (ex.StatusCode == 401)
        {
            // A stale key here must NOT be swallowed: returning null would mis-classify this device
            // as "not the pinned one" and block the run. Let it propagate so the caller's
            // WithDeviceKeyAsync self-heals (clear + re-register + retry).
            throw;
        }
        catch (Exception ex)
        {
            _logger.Log(LogLevel.Warning, "Sync now: could not resolve this device's id.", ex);
        }

        return _deviceId;
    }

    public async Task<IReadOnlyList<string>> UnlinkAccountAsync(string accountRef, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(accountRef)) throw new ArgumentNullException(nameof(accountRef));
        var bearer = await RequireBearerAsync(ct);
        return await _pairs.UnlinkAccountAsync(bearer, accountRef, ct);
    }

    public async Task<DeviceInfo> GetDeviceAsync(CancellationToken ct = default)
    {
        // Wrapped so opening the dashboard (which loads the device card) self-heals a stale key:
        // a 401 clears it, re-registers, and retries — recovering a device stuck on a bad key.
        return await WithDeviceKeyAsync((key, c) => _pairs.GetDeviceMeAsync(key, c), ct);
    }

    public async Task<DeviceInfo> RenameDeviceAsync(string name, CancellationToken ct = default)
    {
        if (name == null) throw new ArgumentNullException(nameof(name));
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            throw new InvalidOperationException("Device name is required.");

        var info = await WithDeviceKeyAsync((key, c) => _pairs.RenameDeviceAsync(key, trimmed, c), ct);

        // Keep AppSettings.DeviceName in sync so a later re-register uses the renamed value as its
        // fallback. The hot rename above is the source of truth for the live device; this just
        // mirrors it into the local config.
        try
        {
            var current = _settingsRepo.TryLoad(_settingsPath) ?? new AppSettings();
            current.DeviceName = info.Name;
            _settingsRepo.Save(current, _settingsPath);
        }
        catch
        {
            // A config-mirror failure must not fail the rename: the server is already updated.
        }

        return info;
    }

    public async Task<bool> CheckDeviceNameAsync(string name, CancellationToken ct = default)
    {
        if (name == null) throw new ArgumentNullException(nameof(name));
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return false;

        return await WithDeviceKeyAsync((key, c) => _pairs.CheckDeviceNameAvailableAsync(key, trimmed, c), ct);
    }

    public async Task<string?> GenerateTxtAsync(string requestJson, CancellationToken ct = default)
    {
        if (requestJson == null) throw new ArgumentNullException(nameof(requestJson));

        // The UI sends the export parameters CalExport Simple mode supports. A blank/empty
        // payload falls back to the current month with cancelled included (the pre-params
        // behaviour) so an unparameterised call still works.
        var now = DateTime.Now;
        GenerateTxtDto dto;
        if (string.IsNullOrWhiteSpace(requestJson))
        {
            dto = new GenerateTxtDto();
        }
        else
        {
            // A malformed payload degrades to the current-month/year fallback rather than
            // throwing, mirroring UiBridge.ParseCreateCalendarPayload's defensive parsing.
            try
            {
                dto = Newtonsoft.Json.JsonConvert.DeserializeObject<GenerateTxtDto>(requestJson) ?? new GenerateTxtDto();
            }
            catch (Newtonsoft.Json.JsonException)
            {
                dto = new GenerateTxtDto();
            }
        }

        var year = dto.Year ?? now.Year;
        var month = dto.Month ?? now.Month;
        if (month is < 1 or > 12)
            throw new InvalidOperationException("Month must be between 1 and 12.");
        if (year is < 1 or > 9999)
            throw new InvalidOperationException("Year is out of range.");

        var includeCancelled = dto.IncludeCancelled ?? true;

        // CalExport reads LOCAL Outlook Classic calendars only (no Graph path), filtered by
        // display name. When the UI supplies calendar names use them, otherwise fall back to the
        // device's configured names (null/empty → "all calendars").
        var calendarNames = dto.CalendarNames is { Count: > 0 } ? dto.CalendarNames : _engineSettings.CalendarNames;

        var suggested = $"ZyncMaster-{year:D4}-{month:D2}.txt";
        var path = await _saveDialog(suggested);
        if (string.IsNullOrEmpty(path))
            return null; // user cancelled

        await _txtExporter.ExportAsync(year, month, calendarNames, includeCancelled, path, ct);
        return path;
    }

    public async Task<string?> ExportSourceTxtAsync(string requestJson, CancellationToken ct = default)
    {
        if (requestJson == null) throw new ArgumentNullException(nameof(requestJson));

        ExportSourceTxtDto dto;
        try
        {
            dto = Newtonsoft.Json.JsonConvert.DeserializeObject<ExportSourceTxtDto>(requestJson)
                  ?? throw new InvalidOperationException("Invalid export-source-txt request.");
        }
        catch (Newtonsoft.Json.JsonException)
        {
            throw new InvalidOperationException("Invalid export-source-txt request.");
        }

        if (string.IsNullOrEmpty(dto.PairId))
            throw new InvalidOperationException("export-source-txt request is missing 'pairId'.");

        var now = DateTime.Now;
        var year = dto.Year ?? now.Year;
        var month = dto.Month ?? now.Month;
        if (month is < 1 or > 12)
            throw new InvalidOperationException("Month must be between 1 and 12.");
        if (year is < 1 or > 9999)
            throw new InvalidOperationException("Year is out of range.");

        var includeCancelled = dto.IncludeCancelled ?? true;

        // Ask for the destination FIRST: if the user cancels the save dialog we return without ever
        // contacting the server, so a cancelled export never wastes a Graph read. Only once we have a
        // real path do we read the source and write the .txt.
        var suggested = $"ZyncMaster-{year:D4}-{month:D2}.txt";
        var path = await _saveDialog(suggested);
        if (string.IsNullOrEmpty(path))
            return null; // user cancelled — no server read

        // The source calendar is online; the server reads it and returns the Simple-mode .txt.
        var bearer = await RequireBearerAsync(ct);
        var txt = await _pairs.ExportSourceTxtAsync(bearer, dto.PairId, year, month, includeCancelled, ct);

        await File.WriteAllTextAsync(path, txt, ct);
        return path;
    }

    public Task<AppCapabilities> GetCapabilitiesAsync(CancellationToken ct = default)
        => Task.FromResult(new AppCapabilities { OutlookCom = _comProbe.IsAvailable() });

    public Task<bool> GetAutoStartAsync(CancellationToken ct = default)
        => Task.FromResult(_autoStart.IsEnabled());

    public Task SetAutoStartAsync(bool enabled, CancellationToken ct = default)
    {
        if (enabled)
            _autoStart.Enable(_autoStartExePath, "--silent");
        else
            _autoStart.Disable();
        return Task.CompletedTask;
    }

    // ---------------- Identity (sign-in) lifecycle ----------------

    public Task<IdentityState> GetIdentityStateAsync(CancellationToken ct = default)
        => _identity.GetIdentityStateAsync(ct);

    public async Task<LoginOutcome> LoginAsync(string provider, string? email, CancellationToken ct = default)
    {
        switch ((provider ?? "").Trim().ToLowerInvariant())
        {
            case "microsoft":
                return await _identity.LoginWithMicrosoftAsync(ct);

            case "magic-link":
            {
                if (string.IsNullOrWhiteSpace(email))
                    return LoginOutcome.Fail("Enter an email address to receive a sign-in link.");

                var requested = await _identity.RequestMagicLinkAsync(email, ct);
                // The link is emailed; the sign-in completes later on the loopback callback. Report
                // success-with-no-state so the UI can show "check your email"; the next
                // GetIdentityStateAsync poll surfaces the signed-in user once the link is clicked.
                return requested.Requested
                    ? new LoginOutcome(true, null, null)
                    : LoginOutcome.Fail(requested.Error ?? "Could not send the sign-in link.");
            }

            default:
                return LoginOutcome.Fail($"Unknown sign-in provider '{provider}'.");
        }
    }

    public Task CancelLoginAsync(CancellationToken ct = default)
    {
        _identity.CancelLogin();
        return Task.CompletedTask;
    }

    public Task SignOutAsync(CancellationToken ct = default) => _identity.SignOutAsync(ct);

    // ---------------- Calendar-account connection lifecycle ----------------

    public Task<ConnectCalendarOutcome> ConnectCalendarAsync(string scope, CancellationToken ct = default)
        => _calendarConnect.ConnectCalendarAsync(scope, ct);

    public Task<IReadOnlyList<CalendarAccountSummary>> ListCalendarAccountsAsync(CancellationToken ct = default)
        => _calendarConnect.ListCalendarAccountsAsync(ct);

    // CalendarConnectService.CancelConnect is synchronous (it just cancels the in-flight CTS and
    // stops the loopback), so wrap the completed task exactly like CancelLoginAsync does.
    public Task CancelConnectAsync(CancellationToken ct = default)
    {
        _calendarConnect.CancelConnect();
        return Task.CompletedTask;
    }

    // Opens the bundled open-source notices in the OS default viewer (Notepad for .txt) via the shell,
    // mirroring the WebView host's external-link handling. Best-effort: a missing file or no viewer is
    // swallowed so it never bubbles into a bridge error. The file is copied next to the exe by the
    // App csproj, so AppContext.BaseDirectory always points at it for a published build.
    public Task OpenLicensesAsync(CancellationToken ct = default)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
            if (File.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { /* no viewer / blocked: swallow, opening notices must never crash the bridge */ }
        return Task.CompletedTask;
    }

    // Auto-registers THIS device against the server using the signed-in identity bearer, so the
    // device has an api key for every later device-key-gated call (Sync now, heartbeat, getDevice,
    // rename). Idempotent and best-effort:
    //   * if a device key is already stored -> no-op (we register ONCE per installation; registering
    //     again would create a second server-side device for the same machine);
    //   * if there is no signed-in identity (no bearer) -> silent no-op (the App calls this after a
    //     successful sign-in, but a stray call before sign-in must not throw);
    //   * any failure (network, 401, server error) -> logged as a Warning and swallowed, so it can
    //     never break App boot or the post-login flow. The next call simply retries.
    // Returns the freshly persisted key, or null when nothing was registered.
    public async Task<string?> EnsureDeviceRegisteredAsync(CancellationToken ct = default)
    {
        // Idempotent fast path: a device that already has a key is already registered.
        var existing = await _keys.LoadAsync(ct);
        if (!string.IsNullOrEmpty(existing))
            return existing;

        // Registration is brokered by the signed-in user's identity bearer. With no identity we
        // simply cannot register yet — that is the normal "not signed in" state, not an error.
        var tokens = await _identityCache.LoadAsync(ct);
        if (tokens is null || string.IsNullOrEmpty(tokens.AccessToken))
            return null;

        await _registerGate.WaitAsync(ct);
        try
        {
            // Double-checked: a concurrent caller may have registered AND saved a key while we waited
            // for the gate. Reuse it instead of registering again (a second register would re-key the
            // server device and orphan the key the other caller just stored — the 401-after-register
            // bug).
            existing = await _keys.LoadAsync(ct);
            if (!string.IsNullOrEmpty(existing))
                return existing;

            return await RegisterCoreAsync(ct);
        }
        finally
        {
            _registerGate.Release();
        }
    }

    // The actual register-and-persist. MUST be called while holding _registerGate. Returns the freshly
    // persisted key, or null when there is no identity / the server returned no key. Never throws on a
    // transient failure (logged + null) so boot / post-login is never broken.
    private async Task<string?> RegisterCoreAsync(CancellationToken ct)
    {
        var tokens = await _identityCache.LoadAsync(ct);
        if (tokens is null || string.IsNullOrEmpty(tokens.AccessToken))
            return null;

        try
        {
            var deviceName = ResolveDeviceName();
            var registration = await _pairs.RegisterDeviceAsync(tokens.AccessToken, deviceName, ct);

            if (registration is null || string.IsNullOrEmpty(registration.ApiKey))
            {
                _logger.Log(LogLevel.Warning, "Device registration returned no api key; device is not registered.");
                return null;
            }

            await _keys.SaveAsync(registration.ApiKey, ct);
            if (!string.IsNullOrEmpty(registration.DeviceId))
                _deviceId = registration.DeviceId;
            _logger.Log(LogLevel.Info, $"Device registered (deviceId={registration.DeviceId}).");
            return registration.ApiKey;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown — let the caller observe the cancellation.
            throw;
        }
        catch (Exception ex)
        {
            // Never break boot / post-login on a transient failure; the next call retries.
            _logger.Log(LogLevel.Warning, "Device registration failed; the device will retry later.", ex);
            return null;
        }
    }

    // Runs a device-api-key operation and SELF-HEALS a stale key: if the call returns 401 the stored
    // key is no longer valid (e.g. a prior concurrent registration left a mismatched key, or the
    // server re-keyed the device), so clear it, re-register to mint a fresh one, and retry ONCE. This
    // recovers a device that is stuck on a bad key without the user having to sign out / reinstall.
    private async Task<T> WithDeviceKeyAsync<T>(Func<string, CancellationToken, Task<T>> op, CancellationToken ct)
    {
        var key = await RequireKeyAsync(ct);
        try
        {
            return await op(key, ct);
        }
        catch (SyncClientException ex) when (ex.StatusCode == 401)
        {
            _logger.Log(LogLevel.Warning, "Device key was rejected (401); re-registering this device and retrying once.");
            var fresh = await ReregisterDeviceAsync(key, ct);
            return await op(fresh, ct);
        }
    }

    // Clears the rejected key and registers the device anew, returning the fresh key. Serialised on
    // the same gate as EnsureDeviceRegisteredAsync, and a no-op clear when another concurrent recovery
    // already replaced the rejected key (so two 401s racing do not wipe a just-minted good key).
    private async Task<string> ReregisterDeviceAsync(string rejectedKey, CancellationToken ct)
    {
        await _registerGate.WaitAsync(ct);
        try
        {
            var current = await _keys.LoadAsync(ct);
            // Another recovery already swapped in a new key while we waited — use it, do not re-register.
            if (!string.IsNullOrEmpty(current) && !string.Equals(current, rejectedKey, StringComparison.Ordinal))
                return current;

            await _keys.ClearAsync(ct);
            _deviceId = null;
            var fresh = await RegisterCoreAsync(ct);
            if (string.IsNullOrEmpty(fresh))
                throw new InvalidOperationException(
                    "This device could not be re-registered. Check your connection and try again.");
            return fresh;
        }
        finally
        {
            _registerGate.Release();
        }
    }

    // The friendly device name used at registration: the user-configured name when set, else the
    // machine's hostname. EngineSettings.DeviceName already defaults to Environment.MachineName via
    // the resolver, so this is just a defensive fallback for an empty/whitespace value.
    private string ResolveDeviceName()
    {
        var name = _engineSettings.DeviceName;
        return string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name.Trim();
    }

    private async Task<string> RequireKeyAsync(CancellationToken ct)
    {
        var key = await _keys.LoadAsync(ct);

        // Self-heal: a signed-in device with no key is the exact bug this fix closes — the device
        // was never registered after sign-in. Try to register it ONCE here and reload, so "Sync
        // now" / getDevice / rename / heartbeat auto-cure instead of failing with "no device key".
        if (string.IsNullOrEmpty(key))
        {
            key = await EnsureDeviceRegisteredAsync(ct);
        }

        if (string.IsNullOrEmpty(key))
        {
            // Distinguish "not signed in" (register first by signing in) from "signed in but the
            // registration is not available yet" (a transient server/network failure).
            var tokens = await _identityCache.LoadAsync(ct);
            if (tokens is null || string.IsNullOrEmpty(tokens.AccessToken))
            {
                _logger.Log(LogLevel.Warning, "Operation requires a registered device, but no identity is present.");
                throw new InvalidOperationException("Sign in to register this device before syncing.");
            }

            _logger.Log(LogLevel.Warning, "Operation requires a paired device, but no device key is present.");
            throw new InvalidOperationException("This device could not be registered yet. Check your connection and try again.");
        }
        return key;
    }

    // The signed-in user's identity access token for the HUMAN-only accounts/pairs management
    // surface (the server gates it with RequireCookieOrIdentityBearer — the device api key is NOT
    // accepted). The App gates sign-in before showing these screens, so a missing identity here is
    // an invariant violation; fail cleanly with a clear message rather than sending an empty bearer.
    private async Task<string> RequireBearerAsync(CancellationToken ct)
    {
        var tokens = await _identityCache.LoadAsync(ct);
        if (tokens is null || string.IsNullOrEmpty(tokens.AccessToken))
        {
            _logger.Log(LogLevel.Warning, "Operation requires sign-in, but no identity token is present.");
            throw new InvalidOperationException("You must be signed in to manage accounts and sync pairs.");
        }
        return tokens.AccessToken;
    }

    // ---------------- Clipboard module (Plan 2/3) ----------------

    // Newest-first clipboard history. Text items are DECRYPTED here with the local text key before
    // they leave the engine (the UI never sees ciphertext); a Text item we cannot decrypt yet (key
    // not admitted) is returned with Text=null so the viewer can still show a placeholder rather than
    // dropping the row. Image items get a best-effort PNG data URI preview (the thumbnail bytes when
    // present, else null — DIB->PNG conversion is intentionally NOT attempted here).
    public async Task<IReadOnlyList<ClipboardHistoryItem>> GetClipboardHistoryAsync(CancellationToken ct = default)
    {
        if (_clipboardTransport is null)
            return new List<ClipboardHistoryItem>();

        var raw = await _clipboardTransport.GetHistoryAsync(ct).ConfigureAwait(false);

        byte[]? textKey = null;
        if (_clipboardKeys is not null)
            textKey = await _clipboardKeys.LoadTextKeyAsync(ct).ConfigureAwait(false);

        var items = new List<ClipboardHistoryItem>(raw.Count);
        foreach (var entry in raw)
            items.Add(ToHistoryItem(entry, textKey));
        return items;
    }

    // Maps an Engine ClipboardEntry to the wire item, decrypting Text and building the image preview.
    // Shared by GetClipboardHistoryAsync and the live "clipboard:item" push (BuildPushItem).
    internal ClipboardHistoryItem ToHistoryItem(ClipboardEntry entry, byte[]? textKey)
    {
        ArgumentNullException.ThrowIfNull(entry);

        string? text = null;
        string? preview = null;

        if (entry.Type == ClipboardEntryType.Text)
        {
            // Prefer an already-decrypted Text (the push path decrypts via the ClipboardService);
            // otherwise decrypt the ciphertext here. A wrong/absent key leaves Text null — no leak,
            // no throw.
            if (entry.Text is not null)
            {
                text = entry.Text;
            }
            else if (textKey is not null && entry.CipherText is { Length: > 0 })
            {
                try { text = TextCrypto.Decrypt(textKey, entry.CipherText); }
                catch (System.Security.Cryptography.CryptographicException) { text = null; }
            }
        }
        else
        {
            preview = BuildImagePreview(entry);
        }

        return new ClipboardHistoryItem
        {
            Id = entry.Id,
            Type = entry.Type.ToString(),
            Text = text,
            ImagePreviewDataUri = preview,
            SizeBytes = entry.SizeBytes,
            CreatedUtc = entry.CreatedUtc,
            OriginDeviceId = entry.OriginDeviceId,
            OriginDeviceName = entry.OriginDeviceName,
        };
    }

    // Best-effort small PNG data URI for an image item. Only the stored Thumbnail (already a PNG when
    // the server/origin produced one) is surfaced; the raw CF_DIB ImageBytes are NOT converted here
    // (DIB->PNG is a known follow-up). Returns null when there is no cheap preview.
    private static string? BuildImagePreview(ClipboardEntry entry)
    {
        if (entry.Thumbnail is { Length: > 0 } thumb)
            return "data:image/png;base64," + Convert.ToBase64String(thumb);
        return null;
    }

    // The user's devices for the clipboard Devices view. The roster (id + name + last-seen) comes
    // from the server under the device api key; per-device clipboard settings come from the transport
    // (GET /api/clipboard/settings/{deviceId}). Editing works for offline devices too, so a device's
    // settings are fetched regardless of online state. A per-device settings fetch failure degrades
    // to that device's defaults rather than failing the whole view.
    public async Task<ClipboardDevicesView> GetClipboardDevicesAsync(CancellationToken ct = default)
    {
        if (_clipboardDevices is null || _clipboardTransport is null)
            return new ClipboardDevicesView { ThisDeviceId = _clipboardDeviceId };

        var rows = await WithDeviceKeyAsync((key, c) => _clipboardDevices.ListDevicesAsync(key, c), ct)
            .ConfigureAwait(false);

        var now = _clock.UtcNow;
        var views = new List<ClipboardDeviceView>(rows.Count);
        foreach (var row in rows)
        {
            ClipboardSettings settings;
            try
            {
                // Self-heal on a rotated/rejected device key: WithDeviceKeyAsync re-registers and retries
                // once on 401 (the transport reads the refreshed key from the same store), so a settings
                // fetch recovers the persisted values instead of silently degrading to defaults. The op
                // ignores the key argument because the transport carries its own api-key provider.
                settings = await WithDeviceKeyAsync(
                    (_, c) => _clipboardTransport.GetSettingsAsync(row.Id, c), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Warning, $"Clipboard devices: could not load settings for device '{row.Id}'.", ex);
                settings = new ClipboardSettings();
            }

            views.Add(new ClipboardDeviceView
            {
                Id = row.Id,
                Name = row.Name,
                Online = row.LastSeenUtc is { } seen && now - seen <= DeviceOnlineWindow,
                IsThis = !string.IsNullOrEmpty(_clipboardDeviceId)
                         && string.Equals(row.Id, _clipboardDeviceId, StringComparison.Ordinal),
                Settings = ToSettingsView(settings),
            });
        }

        return new ClipboardDevicesView { ThisDeviceId = _clipboardDeviceId, Devices = views };
    }

    private static ClipboardSettingsView ToSettingsView(ClipboardSettings s) => new()
    {
        AutoSync = s.AutoSync,
        Send = s.Send,
        Receive = s.Receive,
        ViewerHotkey = s.ViewerHotkey,
        Density = s.Density,
        ShowHints = s.ShowHints,
    };

    // Persists one device's clipboard settings via the server PATCH. Validates density ("rich" |
    // "mini") before sending; an invalid value throws so the bridge surfaces a clean error. If the
    // edited device is THIS device, the live in-process settings are updated too so the running
    // capture/apply pipeline honours the change immediately (no restart).
    public async Task UpdateClipboardSettingsAsync(string payloadJson, CancellationToken ct = default)
    {
        if (payloadJson == null) throw new ArgumentNullException(nameof(payloadJson));
        if (_clipboardTransport is null)
            throw new InvalidOperationException("Clipboard is not available on this device.");

        UpdateClipboardSettingsDto dto;
        try
        {
            dto = Newtonsoft.Json.JsonConvert.DeserializeObject<UpdateClipboardSettingsDto>(payloadJson)
                  ?? throw new InvalidOperationException("Invalid clipboard-settings request.");
        }
        catch (Newtonsoft.Json.JsonException)
        {
            throw new InvalidOperationException("Invalid clipboard-settings request.");
        }

        var deviceId = (dto.DeviceId ?? "").Trim();
        if (deviceId.Length == 0)
            throw new InvalidOperationException("clipboard-settings request is missing 'deviceId'.");

        var density = (dto.Density ?? "rich").Trim();
        if (!string.Equals(density, "rich", StringComparison.Ordinal)
            && !string.Equals(density, "mini", StringComparison.Ordinal))
            throw new InvalidOperationException("Density must be 'rich' or 'mini'.");

        var settings = new ClipboardSettings
        {
            AutoSync = dto.AutoSync ?? true,
            Send = dto.Send ?? true,
            Receive = dto.Receive ?? true,
            ViewerHotkey = string.IsNullOrWhiteSpace(dto.ViewerHotkey) ? "Ctrl+Win+Q" : dto.ViewerHotkey!.Trim(),
            Density = density,
            ShowHints = dto.ShowHints ?? true,
        };

        await _clipboardTransport.UpdateSettingsAsync(deviceId, settings, ct).ConfigureAwait(false);

        // Mirror onto the live settings when the edited device is this one.
        if (!string.IsNullOrEmpty(_clipboardDeviceId)
            && string.Equals(deviceId, _clipboardDeviceId, StringComparison.Ordinal))
        {
            CurrentClipboardSettings = settings;
        }
    }

    // Resolves the history item by id, sets the OS clipboard from it and pastes it into the
    // previously-focused window, then closes the viewer. Returns false (clean no-op) for an unknown
    // id. Text is decrypted before it is written to the OS clipboard (the sink writes Text/ImageBytes).
    public async Task<bool> PasteClipboardEntryAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
        if (_clipboardTransport is null || _clipboardSink is null)
            return false;

        var history = await _clipboardTransport.GetHistoryAsync(ct).ConfigureAwait(false);
        ClipboardEntry? match = null;
        foreach (var entry in history)
            if (string.Equals(entry.Id, id, StringComparison.Ordinal))
            {
                match = entry;
                break;
            }

        if (match is null)
        {
            _logger.Log(LogLevel.Warning, $"Paste clipboard: item '{id}' was not found in history.");
            return false;
        }

        // Decrypt Text before handing it to the sink (the OS clipboard must get plaintext). An
        // image rides its bytes; nothing to decrypt.
        if (match.Type == ClipboardEntryType.Text && match.Text is null && _clipboardKeys is not null)
        {
            var key = await _clipboardKeys.LoadTextKeyAsync(ct).ConfigureAwait(false);
            if (key is not null && match.CipherText is { Length: > 0 })
            {
                try { match = match with { Text = TextCrypto.Decrypt(key, match.CipherText) }; }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    _logger.Log(LogLevel.Warning, $"Paste clipboard: item '{id}' could not be decrypted.");
                    return false;
                }
            }
        }

        // Guard against a no-op paste: if a Text item still has no plaintext (key not admitted yet) or
        // an image has no bytes, nothing would be written to the OS clipboard. Report failure and leave
        // the viewer open rather than dismissing it with a false "ok" the user reads as a done paste.
        var hasContent = match.Type == ClipboardEntryType.Text
            ? !string.IsNullOrEmpty(match.Text)
            : match.ImageBytes is { Length: > 0 };
        if (!hasContent)
        {
            _logger.Log(LogLevel.Warning, $"Paste clipboard: item '{id}' is not available yet (no content to paste).");
            return false;
        }

        // Close the viewer FIRST so the previously-focused window is restored as the foreground
        // window before the sink reads it (the sink captures GetForegroundWindow at paste time and
        // synthesizes Ctrl+V into it). The viewer's close handler re-asserts the window it captured
        // on open, so by the time the sink pastes, focus is back where the user expects it.
        CloseClipboardViewer?.Invoke();

        // Route through the ClipboardService (when wired) so the dedupe is marked before the OS write
        // and the resulting capture is recognized as an echo and dropped. Without the seam, fall back
        // to the raw sink (no echo suppression).
        var wrote = PasteThroughClipboardService is { } paste
            ? await paste(match, ct).ConfigureAwait(false)
            : await _clipboardSink.PasteIntoFocusedAsync(match, ct).ConfigureAwait(false);
        return wrote;
    }

    // Re-registers the global viewer hotkey and persists it in THIS device's clipboard settings (so a
    // later boot restores it). A blank hotkey throws so the bridge surfaces a clear error. Persisting
    // is best-effort: a failed save must not lose the just-registered binding.
    public async Task SetClipboardHotkeyAsync(string hotkey, CancellationToken ct = default)
    {
        if (hotkey == null) throw new ArgumentNullException(nameof(hotkey));
        var trimmed = hotkey.Trim();
        if (trimmed.Length == 0)
            throw new InvalidOperationException("A hotkey is required.");

        _clipboardHotkey?.Register(trimmed);

        CurrentClipboardSettings = CurrentClipboardSettings with { ViewerHotkey = trimmed };

        if (_clipboardTransport is not null && !string.IsNullOrEmpty(_clipboardDeviceId))
        {
            try
            {
                await _clipboardTransport.UpdateSettingsAsync(_clipboardDeviceId, CurrentClipboardSettings, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Log(LogLevel.Warning, "Set clipboard hotkey: could not persist the new hotkey.", ex);
            }
        }
    }

    // Closes the viewer window (Esc / after a paste). A no-op when no viewer is wired/open.
    public Task CloseClipboardViewerAsync(CancellationToken ct = default)
    {
        CloseClipboardViewer?.Invoke();
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
        _registerGate.Dispose();
        _ownedHttp?.Dispose();
    }

    // ---- request DTOs for the JSON payloads the web layer sends ----

    private sealed class CreatePairDto
    {
        [Newtonsoft.Json.JsonProperty("name")] public string? Name { get; set; }
        [Newtonsoft.Json.JsonProperty("source")] public EndpointDto? Source { get; set; }
        [Newtonsoft.Json.JsonProperty("destination")] public EndpointDto? Destination { get; set; }
        [Newtonsoft.Json.JsonProperty("intervalMin")] public int? IntervalMin { get; set; }
        // Track B — the UI sends its own deviceId here for COM sources so the pair is pinned at
        // creation. Null for non-COM pairs or when the device id is not yet known.
        [Newtonsoft.Json.JsonProperty("pinnedDeviceId")] public string? PinnedDeviceId { get; set; }
    }

    private sealed class UpdatePairDto
    {
        [Newtonsoft.Json.JsonProperty("id")] public string? Id { get; set; }
        [Newtonsoft.Json.JsonProperty("name")] public string? Name { get; set; }
        [Newtonsoft.Json.JsonProperty("intervalMin")] public int? IntervalMin { get; set; }
        [Newtonsoft.Json.JsonProperty("state")] public string? State { get; set; }
        // §F2 endpoint edits: null leaves the existing side unchanged on the server.
        [Newtonsoft.Json.JsonProperty("source")] public EndpointDto? Source { get; set; }
        [Newtonsoft.Json.JsonProperty("destination")] public EndpointDto? Destination { get; set; }
    }

    private sealed class ExportSourceTxtDto
    {
        [Newtonsoft.Json.JsonProperty("pairId")] public string? PairId { get; set; }
        [Newtonsoft.Json.JsonProperty("year")] public int? Year { get; set; }
        [Newtonsoft.Json.JsonProperty("month")] public int? Month { get; set; }
        [Newtonsoft.Json.JsonProperty("includeCancelled")] public bool? IncludeCancelled { get; set; }
    }

    private sealed class UpdateClipboardSettingsDto
    {
        [Newtonsoft.Json.JsonProperty("deviceId")] public string? DeviceId { get; set; }
        [Newtonsoft.Json.JsonProperty("autoSync")] public bool? AutoSync { get; set; }
        [Newtonsoft.Json.JsonProperty("send")] public bool? Send { get; set; }
        [Newtonsoft.Json.JsonProperty("receive")] public bool? Receive { get; set; }
        [Newtonsoft.Json.JsonProperty("viewerHotkey")] public string? ViewerHotkey { get; set; }
        [Newtonsoft.Json.JsonProperty("density")] public string? Density { get; set; }
        [Newtonsoft.Json.JsonProperty("showHints")] public bool? ShowHints { get; set; }
    }

    private sealed class GenerateTxtDto
    {
        [Newtonsoft.Json.JsonProperty("year")] public int? Year { get; set; }
        [Newtonsoft.Json.JsonProperty("month")] public int? Month { get; set; }
        [Newtonsoft.Json.JsonProperty("includeCancelled")] public bool? IncludeCancelled { get; set; }
        [Newtonsoft.Json.JsonProperty("calendarNames")] public System.Collections.Generic.List<string>? CalendarNames { get; set; }
    }

    private sealed class EndpointDto
    {
        [Newtonsoft.Json.JsonProperty("provider")] public string? Provider { get; set; }
        [Newtonsoft.Json.JsonProperty("accountRef")] public string? AccountRef { get; set; }
        [Newtonsoft.Json.JsonProperty("calendarId")] public string? CalendarId { get; set; }
        [Newtonsoft.Json.JsonProperty("calendarName")] public string? CalendarName { get; set; }

        // Feature 2 source selection. Omitted by the UI for the destination and for a single-calendar
        // source, so a null list / false flag preserves the legacy single-calendar behaviour.
        [Newtonsoft.Json.JsonProperty("allCalendars")] public bool? AllCalendars { get; set; }
        [Newtonsoft.Json.JsonProperty("calendarIds")] public System.Collections.Generic.List<string>? CalendarIds { get; set; }
        [Newtonsoft.Json.JsonProperty("calendarNames")] public System.Collections.Generic.List<string>? CalendarNames { get; set; }

        public Endpoint ToEndpoint() => new()
        {
            Provider = Provider ?? "",
            AccountRef = AccountRef,
            CalendarId = CalendarId ?? "",
            CalendarName = CalendarName ?? "",
            AllCalendars = AllCalendars ?? false,
            CalendarIds = CalendarIds is { Count: > 0 } ? CalendarIds : null,
            CalendarNames = CalendarNames is { Count: > 0 } ? CalendarNames : null,
        };
    }
}
