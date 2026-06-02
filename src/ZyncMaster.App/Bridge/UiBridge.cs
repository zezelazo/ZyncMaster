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
            case "createPair":
            {
                var pair = await _engine.CreatePairAsync(message.Payload ?? "", ct);
                return JsonSerializer.Serialize(pair, JsonOptions);
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
            case "runPairNow":
            {
                var result = await _engine.RunPairNowAsync(UnwrapString(message.Payload), ct);
                return JsonSerializer.Serialize(result, JsonOptions);
            }
            case "unlinkAccount":
            {
                var affected = await _engine.UnlinkAccountAsync(UnwrapString(message.Payload), ct);
                return JsonSerializer.Serialize(new { affectedPairIds = affected }, JsonOptions);
            }
            case "generateTxt":
            {
                var path = await _engine.GenerateTxtAsync(ct);
                return JsonSerializer.Serialize(new { cancelled = path == null, path }, JsonOptions);
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
