using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ZyncMaster.App.State;

namespace ZyncMaster.App.Bridge;

// Routes messages between the web UI and the engine. It subscribes to the transport's
// Received event, parses each inbound BridgeMessage, dispatches on Action, and sends a
// BridgeReply carrying the same CorrelationId. Every handler is wrapped so that a
// failure becomes Ok=false with the exception message — an exception never escapes the
// bridge and never kills the transport.
//
// PushStatus sends an unsolicited {"event":"status", "payload": <AppStatus>} message so
// the host can drive the UI after each sync cycle without the UI polling.
public sealed class UiBridge
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly IBridgeTransport _transport;
    private readonly IEngineActions _engine;
    private readonly Func<IWindowControl?>? _windowProvider;

    public UiBridge(IBridgeTransport transport, IEngineActions engine, Func<IWindowControl?>? windowProvider = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _windowProvider = windowProvider;
        _transport.Received += OnReceived;
    }

    // Sends an unsolicited status event to the web layer.
    public void PushStatus(AppStatus status)
    {
        if (status == null) throw new ArgumentNullException(nameof(status));

        var envelope = new
        {
            @event = "status",
            payload = status,
        };
        _transport.Send(JsonSerializer.Serialize(envelope, JsonOptions));
    }

    // Sends an unsolicited "clipboard:item" event carrying one history-item, so the clipboard viewer
    // updates live when a new item arrives over the WebSocket (no refresh). Same envelope shape as
    // PushStatus: { event, payload }. The item's Text is already DECRYPTED by the caller.
    public void PushClipboardItem(ClipboardHistoryItem item)
    {
        if (item == null) throw new ArgumentNullException(nameof(item));

        var envelope = new
        {
            @event = "clipboard:item",
            payload = item,
        };
        _transport.Send(JsonSerializer.Serialize(envelope, JsonOptions));
    }

    // Sends an unsolicited "clipboard:presence" event so an open clipboard devices/settings screen can
    // refresh the online dots + "(N online)" count in near-real-time across the user's windows. The
    // payload carries the current online-device ids; the UI treats the event as a "re-fetch the roster"
    // signal (it re-queries getClipboardDevices rather than trusting this list as the whole view).
    public void PushClipboardPresence(System.Collections.Generic.IReadOnlyList<string> onlineDeviceIds)
    {
        var envelope = new
        {
            @event = "clipboard:presence",
            payload = new { onlineDeviceIds = onlineDeviceIds ?? System.Array.Empty<string>() },
        };
        _transport.Send(JsonSerializer.Serialize(envelope, JsonOptions));
    }

    // Sends an unsolicited "clipboard:settings" event when one device's per-device clipboard settings
    // changed on the server (a sibling window edited send/receive/autoSync). The UI uses it as a signal
    // to re-fetch the roster so the affected device's toggles update live; the deviceId + settings are
    // included so a future UI could patch that one row without a full re-fetch.
    public void PushClipboardSettings(string deviceId, ZyncMaster.Engine.ClipboardSettings settings)
    {
        if (deviceId == null) throw new ArgumentNullException(nameof(deviceId));
        if (settings == null) throw new ArgumentNullException(nameof(settings));

        var envelope = new
        {
            @event = "clipboard:settings",
            payload = new { deviceId, settings = EngineActions.ToSettingsView(settings) },
        };
        _transport.Send(JsonSerializer.Serialize(envelope, JsonOptions));
    }

    private void OnReceived(string json)
    {
        // Fire-and-forget: inbound dispatch is async but the transport callback is sync.
        // Any failure is captured and turned into a reply, so nothing escapes here.
        _ = DispatchAsync(json);
    }

    private async Task DispatchAsync(string json)
    {
        BridgeMessage? message = null;
        try
        {
            message = JsonSerializer.Deserialize<BridgeMessage>(json, JsonOptions);
        }
        catch (JsonException)
        {
            // Malformed inbound JSON: we have no correlation id to reply to. Drop it.
            return;
        }

        if (message == null || string.IsNullOrEmpty(message.Action))
            return;

        var correlationId = message.CorrelationId ?? "";

        try
        {
            var payload = await HandleAsync(message, CancellationToken.None);
            Reply(new BridgeReply { CorrelationId = correlationId, Ok = true, Payload = payload });
        }
        catch (Exception ex)
        {
            Reply(new BridgeReply { CorrelationId = correlationId, Ok = false, Error = ex.Message });
        }
    }

    // Returns the serialized result payload for actions that produce one, else null.
    private async Task<string?> HandleAsync(BridgeMessage message, CancellationToken ct)
    {
        switch (message.Action)
        {
            case "checkServerHealth":
            {
                var health = await _engine.CheckServerHealthAsync(ct);
                return JsonSerializer.Serialize(health, JsonOptions);
            }
            case "getStatus":
            {
                var status = await _engine.GetStatusAsync(ct);
                return JsonSerializer.Serialize(status, JsonOptions);
            }
            case "syncNow":
            {
                var result = await _engine.SyncNowAsync(ct);
                return JsonSerializer.Serialize(result, JsonOptions);
            }
            case "pair":
            {
                var outcome = await _engine.PairAsync(ct);
                return JsonSerializer.Serialize(outcome, JsonOptions);
            }
            case "saveConfig":
            {
                await _engine.SaveConfigAsync(message.Payload ?? "", ct);
                return null;
            }
            case "setPaused":
            {
                var paused = ParseBool(message.Payload);
                await _engine.SetPausedAsync(paused, ct);
                return null;
            }
            // ---------------- WS3: sync-pair lifecycle ----------------
            case "listAccounts":
            {
                var accounts = await _engine.ListAccountsAsync(ct);
                return JsonSerializer.Serialize(accounts, JsonOptions);
            }
            case "listCalendars":
            {
                var calendars = await _engine.ListCalendarsAsync(UnwrapString(message.Payload), ct);
                return JsonSerializer.Serialize(calendars, JsonOptions);
            }
            case "listLocalCalendars":
            {
                // No payload: enumerate this device's local Outlook (COM) calendars for the wizard's
                // COM source multi-select. Returns a JSON array of display-name strings.
                var localCalendars = await _engine.ListLocalCalendarsAsync(ct);
                return JsonSerializer.Serialize(localCalendars, JsonOptions);
            }
            case "createPair":
            {
                var pair = await _engine.CreatePairAsync(message.Payload ?? "", ct);
                return JsonSerializer.Serialize(pair, JsonOptions);
            }
            case "createCalendar":
            {
                var (accountRef, name) = ParseCreateCalendarPayload(message.Payload);
                var calendar = await _engine.CreateCalendarAsync(accountRef, name, ct);
                return JsonSerializer.Serialize(calendar, JsonOptions);
            }
            case "listPairs":
            {
                var pairs = await _engine.ListPairsAsync(ct);
                return JsonSerializer.Serialize(pairs, JsonOptions);
            }
            case "updatePair":
            {
                var pair = await _engine.UpdatePairAsync(message.Payload ?? "", ct);
                return JsonSerializer.Serialize(pair, JsonOptions);
            }
            case "deletePair":
            {
                await _engine.DeletePairAsync(UnwrapString(message.Payload), ct);
                return null;
            }
            case "cleanupOldDestination":
            {
                // {pairId, destination:{provider, accountRef?, calendarId, calendarName?}} — delete
                // from the OLD destination only the events this pair created. Returns {deleted, failures}.
                var (pairId, destination) = ParsePairDestinationPayload(message.Payload);
                var result = await _engine.CleanupOldDestinationAsync(pairId, destination, ct);
                return JsonSerializer.Serialize(new { deleted = result.Deleted, failures = result.Failures }, JsonOptions);
            }
            case "countManagedInDestination":
            {
                // {pairId, destination:{...}} — count (no delete) the events this pair created in the
                // destination, for the wizard's cleanup confirm. Returns {count}.
                var (pairId, destination) = ParsePairDestinationPayload(message.Payload);
                var count = await _engine.CountManagedInDestinationAsync(pairId, destination, ct);
                return JsonSerializer.Serialize(new { count }, JsonOptions);
            }
            case "runPairNow":
            {
                var result = await _engine.RunPairNowAsync(UnwrapString(message.Payload), ct);
                return JsonSerializer.Serialize(result, JsonOptions);
            }
            case "requestPairSync":
            {
                // Track B — COM pair pinned to ANOTHER device: signal that device to run instead of
                // reading COM locally. Returns { status, deviceName } so the UI can announce the
                // outcome ("requested" / "origin_unavailable" / "local" / "not_com_pinned").
                var result = await _engine.RequestPairSyncAsync(UnwrapString(message.Payload), ct);
                return JsonSerializer.Serialize(result, JsonOptions);
            }
            case "unlinkAccount":
            {
                var affected = await _engine.UnlinkAccountAsync(UnwrapString(message.Payload), ct);
                return JsonSerializer.Serialize(new { affectedPairIds = affected }, JsonOptions);
            }
            case "ensureDevice":
            {
                // Best-effort auto-registration: the UI calls this after a successful sign-in (and at
                // boot when already signed in) so the device gets its api key and the scheduler /
                // heartbeat / Sync-now work without a manual pairing step. Returns { registered: bool }
                // so the UI can fire-and-forget without needing the key itself.
                var key = await _engine.EnsureDeviceRegisteredAsync(ct);
                return JsonSerializer.Serialize(new { registered = !string.IsNullOrEmpty(key) }, JsonOptions);
            }
            case "getDevice":
            {
                var device = await _engine.GetDeviceAsync(ct);
                return JsonSerializer.Serialize(device, JsonOptions);
            }
            case "renameDevice":
            {
                var device = await _engine.RenameDeviceAsync(ParseDeviceName(message.Payload), ct);
                return JsonSerializer.Serialize(device, JsonOptions);
            }
            case "checkDeviceName":
            {
                var available = await _engine.CheckDeviceNameAsync(ParseDeviceName(message.Payload), ct);
                return JsonSerializer.Serialize(new { available }, JsonOptions);
            }
            case "generateTxt":
            {
                // The UI sends the export params {year, month, includeCancelled, calendarNames?};
                // a missing payload falls back to the current month (handled in the engine).
                var path = await _engine.GenerateTxtAsync(message.Payload ?? "", ct);
                return JsonSerializer.Serialize(new { cancelled = path == null, path }, JsonOptions);
            }
            case "exportSourceTxt":
            {
                // {pairId, year, month, includeCancelled} — the server reads the pair's online
                // source calendar and returns the .txt; the engine saves it. Same reply shape as
                // generateTxt so the UI handles both export branches identically.
                var path = await _engine.ExportSourceTxtAsync(message.Payload ?? "", ct);
                return JsonSerializer.Serialize(new { cancelled = path == null, path }, JsonOptions);
            }
            case "getCapabilities":
            {
                var caps = await _engine.GetCapabilitiesAsync(ct);
                return JsonSerializer.Serialize(caps, JsonOptions);
            }
            case "getAutoStart":
            {
                var enabled = await _engine.GetAutoStartAsync(ct);
                return JsonSerializer.Serialize(new { enabled }, JsonOptions);
            }
            case "setAutoStart":
            {
                await _engine.SetAutoStartAsync(ParseBool(message.Payload), ct);
                return null;
            }
            // ---------------- Identity (sign-in) lifecycle ----------------
            case "getIdentityState":
            {
                var state = await _engine.GetIdentityStateAsync(ct);
                return JsonSerializer.Serialize(state, JsonOptions);
            }
            case "login":
            {
                var (provider, email) = ParseLoginPayload(message.Payload);
                var outcome = await _engine.LoginAsync(provider, email, ct);
                return JsonSerializer.Serialize(outcome, JsonOptions);
            }
            case "cancelLogin":
            {
                await _engine.CancelLoginAsync(ct);
                return null;
            }
            case "signOut":
            {
                await _engine.SignOutAsync(ct);
                return null;
            }
            case "openLicenses":
            {
                // Open the bundled open-source notices in the system's default viewer. Fire-and-forget
                // from the UI's perspective; the engine swallows any open failure.
                await _engine.OpenLicensesAsync(ct);
                return null;
            }
            // ---------------- Calendar-account connection lifecycle ----------------
            case "connectCalendar":
            {
                var scope = ParseConnectScope(message.Payload);
                var outcome = await _engine.ConnectCalendarAsync(scope, ct);
                return JsonSerializer.Serialize(outcome, JsonOptions);
            }
            case "upgradeAccountScope":
            {
                var accountId = UnwrapString(message.Payload);
                var outcome = await _engine.UpgradeAccountScopeAsync(accountId, ct);
                return JsonSerializer.Serialize(outcome, JsonOptions);
            }
            case "listCalendarAccounts":
            {
                var accounts = await _engine.ListCalendarAccountsAsync(ct);
                return JsonSerializer.Serialize(accounts, JsonOptions);
            }
            case "cancelConnect":
            {
                await _engine.CancelConnectAsync(ct);
                return null;
            }
            // ---------------- Clipboard module (Plan 2/3) ----------------
            case "getClipboardHistory":
            {
                var history = await _engine.GetClipboardHistoryAsync(ct);
                return JsonSerializer.Serialize(history, JsonOptions);
            }
            case "getClipboardDevices":
            {
                var devices = await _engine.GetClipboardDevicesAsync(ct);
                return JsonSerializer.Serialize(devices, JsonOptions);
            }
            case "updateClipboardSettings":
            {
                // The whole object payload ({deviceId, autoSync, ...}) is forwarded to the engine,
                // which parses + validates it (density) and persists via the server PATCH.
                await _engine.UpdateClipboardSettingsAsync(message.Payload ?? "", ct);
                return JsonSerializer.Serialize(new { ok = true }, JsonOptions);
            }
            case "pasteClipboardEntry":
            {
                // Payload is the item id (a bare/quoted string). Returns {ok|notfound} so the UI can
                // distinguish a successful paste from a stale id.
                var found = await _engine.PasteClipboardEntryAsync(UnwrapString(message.Payload), ct);
                return JsonSerializer.Serialize(new { status = found ? "ok" : "notfound" }, JsonOptions);
            }
            case "setClipboardHotkey":
            {
                // Payload is the hotkey string (a bare/quoted string).
                await _engine.SetClipboardHotkeyAsync(UnwrapString(message.Payload), ct);
                return JsonSerializer.Serialize(new { ok = true }, JsonOptions);
            }
            case "closeClipboardViewer":
            {
                await _engine.CloseClipboardViewerAsync(ct);
                return null;
            }
            // Frameless window controls driven by the custom web title bar (fire-and-forget).
            case "windowMinimize":
                _windowProvider?.Invoke()?.Minimize();
                return null;
            case "windowToggleMaximize":
                _windowProvider?.Invoke()?.ToggleMaximize();
                return null;
            case "windowClose":
                _windowProvider?.Invoke()?.Close();
                return null;
            case "windowDragMove":
                _windowProvider?.Invoke()?.BeginDragMove();
                return null;
            default:
                throw new InvalidOperationException($"Unknown action '{message.Action}'.");
        }
    }

    // Parses a {"provider":"microsoft|magic-link","email":"..."} login payload. A bare string
    // (just the provider) is tolerated too. Missing/blank provider yields "" so the engine
    // surfaces a clear "unknown provider" error rather than the bridge throwing.
    private static (string provider, string? email) ParseLoginPayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return ("", null);

        var trimmed = payload.Trim();
        if (trimmed[0] == '{')
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                var provider = root.TryGetProperty("provider", out var p) ? p.GetString() ?? "" : "";
                var email = root.TryGetProperty("email", out var e) ? e.GetString() : null;
                return (provider, email);
            }
            catch (JsonException)
            {
                return ("", null);
            }
        }

        // Not an object: treat the whole payload as the provider name.
        return (UnwrapString(trimmed), null);
    }

    // Parses a {"scope":"read|readwrite"} connect payload. A bare string ("read"/"readwrite") is
    // tolerated too. Missing/blank defaults to "readwrite" (the common case for a sync destination);
    // the Server validates the value and likewise defaults a blank to read/write.
    private static string ParseConnectScope(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return "readwrite";

        var trimmed = payload.Trim();
        if (trimmed[0] == '{')
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var scope = doc.RootElement.TryGetProperty("scope", out var s) ? s.GetString() : null;
                return string.IsNullOrWhiteSpace(scope) ? "readwrite" : scope!;
            }
            catch (JsonException)
            {
                return "readwrite";
            }
        }

        var bare = UnwrapString(trimmed);
        return string.IsNullOrWhiteSpace(bare) ? "readwrite" : bare;
    }

    // Parses a {"name":"..."} rename payload. A bare string payload is tolerated too (the whole
    // value is taken as the name). Missing/blank yields "" so the engine surfaces a clear
    // "name required" error rather than the bridge throwing.
    private static string ParseDeviceName(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return "";

        var trimmed = payload.Trim();
        if (trimmed[0] == '{')
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                return doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            }
            catch (JsonException)
            {
                return "";
            }
        }

        return UnwrapString(trimmed);
    }

    // Parses a {"accountRef":"...","name":"..."} create-calendar payload. Missing fields yield
    // "" so the engine surfaces a clear validation error rather than the bridge throwing.
    private static (string accountRef, string name) ParseCreateCalendarPayload(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return ("", "");

        var trimmed = payload.Trim();
        if (trimmed[0] == '{')
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                var accountRef = root.TryGetProperty("accountRef", out var a) ? a.GetString() ?? "" : "";
                var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                return (accountRef, name);
            }
            catch (JsonException)
            {
                return ("", "");
            }
        }

        return ("", "");
    }

    // Parses a {"pairId":"...","destination":{provider, accountRef?, calendarId, calendarName?}}
    // payload for the destination-cleanup actions. Missing pairId yields "" so the engine surfaces a
    // clear "missing pairId" error; a missing/blank destination yields an empty Endpoint so the
    // server's validation (provider/calendarId required) returns a clean 400 rather than the bridge
    // throwing. Defensive parse, like the other object payloads.
    private static (string pairId, ZyncMaster.Engine.Endpoint destination) ParsePairDestinationPayload(string? payload)
    {
        var empty = new ZyncMaster.Engine.Endpoint();
        if (string.IsNullOrWhiteSpace(payload))
            return ("", empty);

        var trimmed = payload.Trim();
        if (trimmed[0] != '{')
            return ("", empty);

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            var pairId = root.TryGetProperty("pairId", out var p) ? p.GetString() ?? "" : "";

            if (!root.TryGetProperty("destination", out var d) || d.ValueKind != JsonValueKind.Object)
                return (pairId, empty);

            string? Str(string name) => d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

            var accountRef = Str("accountRef");
            var destination = new ZyncMaster.Engine.Endpoint
            {
                Provider = Str("provider") ?? "",
                AccountRef = string.IsNullOrWhiteSpace(accountRef) ? null : accountRef,
                CalendarId = Str("calendarId") ?? "",
                CalendarName = Str("calendarName") ?? "",
            };
            return (pairId, destination);
        }
        catch (JsonException)
        {
            return ("", empty);
        }
    }

    private static bool ParseBool(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return false;
        var trimmed = payload.Trim().Trim('"');
        return bool.TryParse(trimmed, out var b) && b;
    }

    // A scalar string payload (id / accountRef). The web side stringifies the value, so a
    // bare token arrives as-is; tolerate an accidentally JSON-quoted value too.
    private static string UnwrapString(string? payload)
    {
        if (payload == null)
            return "";
        var trimmed = payload.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            trimmed = trimmed[1..^1];
        return trimmed;
    }

    private void Reply(BridgeReply reply)
        => _transport.Send(JsonSerializer.Serialize(reply, JsonOptions));
}
