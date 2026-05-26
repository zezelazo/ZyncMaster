using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SyncMaster.App.State;

namespace SyncMaster.App.Bridge;

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

    public UiBridge(IBridgeTransport transport, IEngineActions engine)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
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
            default:
                throw new InvalidOperationException($"Unknown action '{message.Action}'.");
        }
    }

    private static bool ParseBool(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return false;
        var trimmed = payload.Trim().Trim('"');
        return bool.TryParse(trimmed, out var b) && b;
    }

    private void Reply(BridgeReply reply)
        => _transport.Send(JsonSerializer.Serialize(reply, JsonOptions));
}
