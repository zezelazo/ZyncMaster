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
    private readonly BasicTxtExporter _txtExporter;
    private readonly IAutoStartManager _autoStart;
    private readonly EngineSettings _engineSettings;
    private readonly Func<string, Task<string?>> _saveDialog;
    private readonly string _autoStartExePath;
    private readonly IdentityLoginService _identity;

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
        BasicTxtExporter txtExporter,
        IAutoStartManager autoStart,
        EngineSettings engineSettings,
        Func<string, Task<string?>> saveDialog,
        string autoStartExePath,
        IdentityLoginService identity,
        HttpClient? ownedHttp = null)
    {
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _pairing = pairing ?? throw new ArgumentNullException(nameof(pairing));
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));
        _settingsRepo = settingsRepo ?? throw new ArgumentNullException(nameof(settingsRepo));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _settingsPath = settingsPath ?? throw new ArgumentNullException(nameof(settingsPath));
        _pairs = pairs ?? throw new ArgumentNullException(nameof(pairs));
        _txtExporter = txtExporter ?? throw new ArgumentNullException(nameof(txtExporter));
        _autoStart = autoStart ?? throw new ArgumentNullException(nameof(autoStart));
        _engineSettings = engineSettings ?? throw new ArgumentNullException(nameof(engineSettings));
        _saveDialog = saveDialog ?? throw new ArgumentNullException(nameof(saveDialog));
        _autoStartExePath = autoStartExePath ?? throw new ArgumentNullException(nameof(autoStartExePath));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
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

    // ---------------- WS3: sync-pair lifecycle ----------------

    public async Task<IReadOnlyList<AccountInfo>> ListAccountsAsync(CancellationToken ct = default)
    {
        var key = await RequireKeyAsync(ct);
        return await _pairs.ListAccountsAsync(key, ct);
    }

    public async Task<IReadOnlyList<CalendarInfo>> ListCalendarsAsync(string accountRef, CancellationToken ct = default)
    {
        if (accountRef == null) throw new ArgumentNullException(nameof(accountRef));
        var key = await RequireKeyAsync(ct);
        return await _pairs.ListCalendarsAsync(key, accountRef, ct);
    }

    public async Task<SyncPair> CreatePairAsync(string requestJson, CancellationToken ct = default)
    {
        if (requestJson == null) throw new ArgumentNullException(nameof(requestJson));
        var key = await RequireKeyAsync(ct);

        var dto = Newtonsoft.Json.JsonConvert.DeserializeObject<CreatePairDto>(requestJson)
                  ?? throw new InvalidOperationException("Invalid create-pair request.");

        var interval = dto.IntervalMin ?? _engineSettings.IntervalMinutes;
        return await _pairs.CreatePairAsync(
            key,
            dto.Name ?? "",
            dto.Source?.ToEndpoint() ?? new Endpoint(),
            dto.Destination?.ToEndpoint() ?? new Endpoint(),
            interval,
            ct);
    }

    public async Task<IReadOnlyList<SyncPair>> ListPairsAsync(CancellationToken ct = default)
    {
        var key = await RequireKeyAsync(ct);
        return await _pairs.ListPairsAsync(key, ct);
    }

    public async Task<SyncPair> UpdatePairAsync(string requestJson, CancellationToken ct = default)
    {
        if (requestJson == null) throw new ArgumentNullException(nameof(requestJson));
        var key = await RequireKeyAsync(ct);

        var dto = Newtonsoft.Json.JsonConvert.DeserializeObject<UpdatePairDto>(requestJson)
                  ?? throw new InvalidOperationException("Invalid update-pair request.");
        if (string.IsNullOrEmpty(dto.Id))
            throw new InvalidOperationException("update-pair request is missing 'id'.");

        return await _pairs.UpdatePairAsync(key, dto.Id, dto.Name, dto.IntervalMin, dto.State, ct);
    }

    public async Task DeletePairAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
        var key = await RequireKeyAsync(ct);
        await _pairs.DeletePairAsync(key, id, ct);
    }

    public async Task<MirrorResult> RunPairNowAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(id)) throw new ArgumentNullException(nameof(id));
        var key = await RequireKeyAsync(ct);
        return await _pairs.RunPairAsync(key, id, ct);
    }

    public async Task<IReadOnlyList<string>> UnlinkAccountAsync(string accountRef, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(accountRef)) throw new ArgumentNullException(nameof(accountRef));
        var key = await RequireKeyAsync(ct);
        return await _pairs.UnlinkAccountAsync(key, accountRef, ct);
    }

    public async Task<string?> GenerateTxtAsync(CancellationToken ct = default)
    {
        var now = DateTime.Now;
        var suggested = $"ZyncMaster-{now:yyyy-MM}.txt";

        var path = await _saveDialog(suggested);
        if (string.IsNullOrEmpty(path))
            return null; // user cancelled

        await _txtExporter.ExportAsync(now.Year, now.Month, _engineSettings.CalendarNames, path, ct);
        return path;
    }

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

    private async Task<string> RequireKeyAsync(CancellationToken ct)
    {
        var key = await _keys.LoadAsync(ct);
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException("This device is not paired yet. Pair it before managing sync pairs.");
        return key;
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

    // ---- request DTOs for the JSON payloads the web layer sends ----

    private sealed class CreatePairDto
    {
        [Newtonsoft.Json.JsonProperty("name")] public string? Name { get; set; }
        [Newtonsoft.Json.JsonProperty("source")] public EndpointDto? Source { get; set; }
        [Newtonsoft.Json.JsonProperty("destination")] public EndpointDto? Destination { get; set; }
        [Newtonsoft.Json.JsonProperty("intervalMin")] public int? IntervalMin { get; set; }
    }

    private sealed class UpdatePairDto
    {
        [Newtonsoft.Json.JsonProperty("id")] public string? Id { get; set; }
        [Newtonsoft.Json.JsonProperty("name")] public string? Name { get; set; }
        [Newtonsoft.Json.JsonProperty("intervalMin")] public int? IntervalMin { get; set; }
        [Newtonsoft.Json.JsonProperty("state")] public string? State { get; set; }
    }

    private sealed class EndpointDto
    {
        [Newtonsoft.Json.JsonProperty("provider")] public string? Provider { get; set; }
        [Newtonsoft.Json.JsonProperty("accountRef")] public string? AccountRef { get; set; }
        [Newtonsoft.Json.JsonProperty("calendarId")] public string? CalendarId { get; set; }
        [Newtonsoft.Json.JsonProperty("calendarName")] public string? CalendarName { get; set; }

        public Endpoint ToEndpoint() => new()
        {
            Provider = Provider ?? "",
            AccountRef = AccountRef,
            CalendarId = CalendarId ?? "",
            CalendarName = CalendarName ?? "",
        };
    }
}
