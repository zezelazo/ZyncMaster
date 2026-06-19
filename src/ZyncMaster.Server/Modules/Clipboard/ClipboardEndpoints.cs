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

            // Defense-in-depth against client echo loops: a publish whose content is byte-identical
            // to the user's NEWEST history item (same type, same payload) is acknowledged
            // idempotently with the EXISTING item's id, without appending a duplicate row and
            // without re-broadcasting it back to the peers. Only the head is deduped; re-copying
            // older content is a legitimate new item.
            //
            // KNOWN LIMITATION — this guard is effectively images-only. Text payloads are E2E
            // ciphertext and TextCrypto uses a fresh random GCM nonce per encryption, so two
            // publishes of the SAME plaintext are never byte-identical here; only an exact
            // retransmit of one blob can match. Do not rely on this check to break a text echo
            // loop — the client-side ClipboardDedupe windows are the real (and only) defense for
            // text. Deduping text server-side would need an opaque client-supplied equality token
            // (e.g. an HMAC of the plaintext under the shared text key — never a plain hash, which
            // would expose short clipboard texts to dictionary attacks).
            var newest = await store.GetNewestAsync(ct);
            if (newest is not null
                && newest.Type == item.Type
                && newest.Payload.AsSpan().SequenceEqual(item.Payload))
            {
                return Results.Ok(new { id = newest.Id });
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

        // POST a lazy blob — the heavy bytes of a File item, streamed to the off-DB blob store under the
        // resolved user (the metadata item is published separately via POST /items). Device-only. Capped
        // at MaxBlobBytes: a larger body is 413, and the client instead publishes a metadata-only "too
        // large to sync" entry. The id is the owning item's id.
        app.MapPost("/api/clipboard/blobs/{id}", async (
            string id,
            HttpContext http,
            IClipboardBlobStore blobs,
            Microsoft.Extensions.Options.IOptions<ClipboardOptions> opts,
            ICurrentUserAccessor currentUser,
            CancellationToken ct) =>
        {
            var max = opts.Value.MaxBlobBytes;
            if (http.Request.ContentLength is { } len && len > max)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            // Raise Kestrel's per-request body cap for THIS upload (the ~28 MB default would truncate a
            // large file). nginx's client_max_body_size must also allow it at the edge.
            var sizeFeature = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false })
                sizeFeature.MaxRequestBodySize = max;
            await blobs.SaveAsync(currentUser.UserId, id, http.Request.Body, ct);
            return Results.Ok(new { id });
        }).RequireApiKey();

        // GET a lazy blob — stream the bytes back, or 404 when missing (never uploaded / evicted by
        // retention). Same read auth as the history so the App, the floating viewer and the web panel
        // can all fetch it on paste.
        app.MapGet("/api/clipboard/blobs/{id}", async (
            string id,
            IClipboardBlobStore blobs,
            ICurrentUserAccessor currentUser,
            CancellationToken ct) =>
        {
            var stream = await blobs.OpenReadAsync(currentUser.UserId, id, ct);
            return stream is null
                ? Results.NotFound()
                : Results.Stream(stream, "application/octet-stream");
        }).RequireCookieOrApiKeyOrIdentityBearer();

        // DELETE one history entry — user-scoped removal, then fan out a {type:"deleted", id} frame to
        // the user's OTHER devices so their clipboard screen / floating viewer drop the row live. Accepts
        // the same three schemes as the reads: the human panel (cookie), the signed-in App (identity
        // bearer) or a paired device (api key) may all delete from the user's own history. The store
        // filters by the resolved userId, so a foreign id is a silent no-op rather than a cross-user
        // delete. The origin device id (the api-key principal's "deviceId" claim, empty under cookie/
        // identity) is excluded from the broadcast — the caller already removed it locally.
        app.MapDelete("/api/clipboard/items/{id}", async (
            string id,
            IClipboardHistoryStore store,
            ClipboardBroadcaster broadcaster,
            ICurrentUserAccessor currentUser,
            HttpContext http,
            CancellationToken ct) =>
        {
            await store.RemoveAsync(id, ct);

            var originDeviceId = http.User.FindFirst("deviceId")?.Value ?? string.Empty;
            await broadcaster.BroadcastDeletedAsync(currentUser.UserId, originDeviceId, id, ct);

            return Results.Ok();
        }).RequireCookieOrApiKeyOrIdentityBearer();

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
        // CALLER's scope and never overwrites the other user's row. The key-admission fields
        // (publicKeyBase64 / needsTextKey) MERGE: when the body omits them the stored values are
        // kept, so a plain preferences save never wipes a device's published key or pending flag.
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

            // GetAsync returns defaults (null key, needsTextKey=false) for a never-stored device,
            // so the merge below is well-defined on first write too.
            var current = await settings.GetAsync(deviceId, ct);

            var updated = new ClipboardDeviceSettings
            {
                DeviceId = deviceId,
                AutoSync = req.AutoSync,
                Send = req.Send,
                Receive = req.Receive,
                ViewerHotkey = req.ViewerHotkey,
                Density = req.Density,
                ShowHints = req.ShowHints,
                PublicKeyBase64 = req.PublicKeyBase64 ?? current.PublicKeyBase64,
                NeedsTextKey = req.NeedsTextKey ?? current.NeedsTextKey,
            };

            await settings.UpsertAsync(updated, ct);

            // Live-push the change to the user's OTHER open windows so their clipboard screen reflects
            // the new send/receive/autoSync flags without a manual refresh. The deviceId being edited is
            // the origin and is excluded. Best-effort: a dead peer socket never fails the PATCH.
            await broadcaster.BroadcastSettingsAsync(currentUser.UserId, deviceId, updated, ct);

            return Results.Ok();
        }).RequireCookieOrApiKeyOrIdentityBearer();

        // GET devices — the clipboard view of the user's device roster: id + name from the device
        // store (user-scoped), live online flag from the WS registry, and the key-admission fields
        // (needsTextKey + publicKeyBase64) from the per-device clipboard settings. A key-holder
        // polls/reads this to find which peer is waiting for the E2E text key and which public key
        // to wrap it against before calling /key/relay.
        app.MapGet("/api/clipboard/devices", async (
            IDeviceStore devices,
            IClipboardSettingsStore settings,
            ClipboardConnectionRegistry registry,
            ICurrentUserAccessor currentUser,
            CancellationToken ct) =>
        {
            var roster = await devices.ListAsync(ct);
            var settingsByDevice = (await settings.ListAsync(ct)).ToDictionary(s => s.DeviceId);
            var online = registry.OnlineDeviceIds(currentUser.UserId).ToHashSet(StringComparer.Ordinal);

            return Results.Ok(roster.Select(d =>
            {
                var s = settingsByDevice.GetValueOrDefault(d.Id);
                return new
                {
                    deviceId = d.Id,
                    name = d.Name,
                    online = online.Contains(d.Id),
                    needsTextKey = s?.NeedsTextKey ?? false,
                    publicKeyBase64 = s?.PublicKeyBase64,
                };
            }));
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
