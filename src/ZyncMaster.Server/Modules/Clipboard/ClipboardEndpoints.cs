using System.Net.WebSockets;

namespace ZyncMaster.Server;

// REST + WebSocket surface for the clipboard module. Mirrors the Devices/Sync modules: a single
// MapClipboardEndpoints extension wires the routes, each one opts into an auth scheme explicitly.
//
// Auth split:
//   * READ surfaces (history GET, settings GET, settings PATCH) accept Cookie OR ApiKey OR
//     IdentityBearer — the human panel (cookie), the desktop App's signed-in user (identity bearer)
//     and a paired device (api key) all read/edit the same user-scoped data, and every store filters
//     by the resolved "userId" claim, so admitting all three is safe.
//   * WRITE-from-device surfaces (publish item, relay key, the WS upgrade) require the device ApiKey:
//     they are the device data path and the origin device id rides the api-key principal/body.
//
// The server treats item payloads as OPAQUE bytes (Text payload is E2E ciphertext) and NEVER
// persists or logs a relayed wrapped key.
public static class ClipboardEndpoints
{
    public static void MapClipboardEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // GET history — newest-first, user-scoped, capped by the store.
        app.MapGet("/api/clipboard/history", async (IClipboardHistoryStore store, CancellationToken ct) =>
        {
            var items = await store.ListAsync(ct);
            return Results.Ok(items.Select(ClipboardDto.ToWire));
        }).RequireCookieOrApiKeyOrIdentityBearer();

        // POST publish — validate, decode, append (user-scoped), then fan out to the user's OTHER
        // online devices over the WS. The origin device id comes from the request body.
        app.MapPost("/api/clipboard/items", async (
            PublishItemRequest req,
            IClipboardHistoryStore store,
            ClipboardBroadcaster broadcaster,
            ICurrentUserAccessor currentUser,
            CancellationToken ct) =>
        {
            var validation = new PublishItemRequestValidator().Validate(req);
            if (!validation.IsValid)
                return Results.ValidationProblem(validation.ToDictionary());

            ClipboardItem item;
            try
            {
                item = ClipboardDto.ToDomain(req, currentUser.UserId);
            }
            catch (FormatException)
            {
                // PayloadBase64 / ThumbnailBase64 not valid base64 — a malformed request, 400.
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["payloadBase64"] = new[] { "Payload must be valid base64." },
                });
            }

            try
            {
                await store.AppendAsync(item, ct);
            }
            catch (ClipboardImageTooLargeException ex)
            {
                // Image over the hard ceiling: reject outright with 413 (payload too large).
                return Results.Json(
                    new { error = "image_too_large", message = ex.Message },
                    statusCode: StatusCodes.Status413PayloadTooLarge);
            }

            // Best-effort fan-out; never blocks/throws on a dead peer socket.
            await broadcaster.BroadcastItemAsync(item, ct);

            return Results.Ok(new { id = item.Id });
        }).RequireApiKey();

        // GET settings — every device row the current user owns (defaults are returned per device
        // only via GetAsync; the listing returns the stored rows).
        app.MapGet("/api/clipboard/settings", async (IClipboardSettingsStore settings, CancellationToken ct) =>
        {
            var rows = await settings.ListAsync(ct);
            return Results.Ok(rows.Select(ClipboardDto.ToWire));
        }).RequireCookieOrApiKeyOrIdentityBearer();

        // GET settings for a SPECIFIC device — what the App actually calls (GetSettingsAsync) on
        // clipboard startup and per device in the roster. The store is user-scoped and GetAsync returns
        // DEFAULTS for a device with no stored row, so a never-configured (or another user's) device id
        // resolves to a fresh settings object rather than 404. Without this route the App got 405 (the
        // path only matched the PATCH below) and the whole clipboard pipeline aborted before the hotkey.
        app.MapGet("/api/clipboard/settings/{deviceId}", async (
            string deviceId, IClipboardSettingsStore settings, CancellationToken ct) =>
        {
            var row = await settings.GetAsync(deviceId, ct);
            return Results.Ok(ClipboardDto.ToWire(row));
        }).RequireCookieOrApiKeyOrIdentityBearer();

        // PATCH settings for a specific device id (any of the user's devices, even offline). The
        // store stamps the ambient user, so a deviceId belonging to another user is created under the
        // CALLER's scope and never overwrites the other user's row.
        app.MapPatch("/api/clipboard/settings/{deviceId}", async (
            string deviceId,
            UpdateClipboardSettingsRequest req,
            IClipboardSettingsStore settings,
            ClipboardBroadcaster broadcaster,
            ICurrentUserAccessor currentUser,
            CancellationToken ct) =>
        {
            var validation = new UpdateClipboardSettingsRequestValidator().Validate(req);
            if (!validation.IsValid)
                return Results.ValidationProblem(validation.ToDictionary());

            var updated = new ClipboardDeviceSettings
            {
                DeviceId = deviceId,
                AutoSync = req.AutoSync,
                Send = req.Send,
                Receive = req.Receive,
                ViewerHotkey = req.ViewerHotkey,
                Density = req.Density,
                ShowHints = req.ShowHints,
            };

            await settings.UpsertAsync(updated, ct);

            // Live-push the change to the user's OTHER open windows so their clipboard screen reflects
            // the new send/receive/autoSync flags without a manual refresh. The deviceId being edited is
            // the origin and is excluded. Best-effort: a dead peer socket never fails the PATCH.
            await broadcaster.BroadcastSettingsAsync(currentUser.UserId, deviceId, updated, ct);

            return Results.Ok();
        }).RequireCookieOrApiKeyOrIdentityBearer();

        // POST key relay — forward a wrapped E2E key to one of the user's other devices, if online.
        // The key is decoded into an opaque envelope and handed to the broadcaster; it is NEVER
        // persisted and NEVER logged. Returns { delivered } so the caller can retry/fallback.
        app.MapPost("/api/clipboard/key/relay", async (
            RelayKeyRequest req,
            ClipboardBroadcaster broadcaster,
            ICurrentUserAccessor currentUser,
            CancellationToken ct) =>
        {
            var validation = new RelayKeyRequestValidator().Validate(req);
            if (!validation.IsValid)
                return Results.ValidationProblem(validation.ToDictionary());

            byte[] wrapped;
            try
            {
                wrapped = Convert.FromBase64String(req.WrappedKeyBase64);
            }
            catch (FormatException)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["wrappedKeyBase64"] = new[] { "Wrapped key must be valid base64." },
                });
            }

            var delivered = await broadcaster.RelayKeyAsync(currentUser.UserId, new WrappedKeyEnvelope
            {
                FromDeviceId = req.FromDeviceId,
                TargetDeviceId = req.TargetDeviceId,
                WrappedKey = wrapped,
            }, ct);

            return Results.Ok(new { delivered });
        }).RequireApiKey();

        // WebSocket upgrade — the live presence + push channel. Identity (userId + deviceId) is
        // resolved AT ACCEPT TIME from the api-key principal, BEFORE the long-lived loop, so no
        // request scope is held across the loop. On accept the connection is registered and presence
        // re-broadcast; on exit (close/error) it is removed and presence re-broadcast.
        app.Map("/ws/clipboard", async (
            HttpContext http,
            ClipboardConnectionRegistry registry,
            ClipboardBroadcaster broadcaster,
            ICurrentUserAccessor currentUser) =>
        {
            if (!http.WebSockets.IsWebSocketRequest)
                return Results.BadRequest(new { error = "not_a_websocket_request" });

            var deviceId = http.User.FindFirst("deviceId")?.Value;
            if (string.IsNullOrWhiteSpace(deviceId))
                return Results.Unauthorized();

            // Capture the identity now — the principal/HttpContext must NOT be read inside the loop.
            var userId = currentUser.UserId;

            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            var conn = new ClipboardConnection { UserId = userId, DeviceId = deviceId, Socket = socket };

            registry.Add(conn);
            await broadcaster.BroadcastPresenceAsync(userId, http.RequestAborted);
            try
            {
                await ClipboardHub.RunReceiveLoopAsync(conn, http.RequestAborted);
            }
            finally
            {
                registry.Remove(userId, deviceId);
                // Re-broadcast presence on departure with a fresh token: RequestAborted may already
                // be cancelled (that is what ended the loop), which would suppress the update.
                await broadcaster.BroadcastPresenceAsync(userId, CancellationToken.None);
            }

            // The response has already been upgraded; return an empty result to satisfy the delegate.
            return Results.Empty;
        }).RequireApiKey();
    }
}
