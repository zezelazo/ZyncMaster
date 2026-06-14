using ZyncMaster.Core;
using ZyncMaster.Graph;

namespace ZyncMaster.Server;

public static class PairEndpoints
{
    public static void MapPairEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Unified account-listing surface. Returns the UNION of the user's NEW per-user account
        // pool (ICalendarAccountStore) and the LEGACY per-UPN store (IConnectedAccountStore). The
        // AccountRef of every returned account is an identifier that the createPair validation AND
        // the sync engine know how to resolve through the adapter (pool-first, legacy fallback):
        //   * pool accounts   -> AccountRef = the pool accountId (resolves in the pool as-is),
        //   * legacy accounts -> AccountRef = the legacy UPN (resolves via its derived id), kept
        //     exactly as before so existing pairs and the legacy listing behaviour are undisturbed.
        // Dedup note: pool accounts use a RANDOM Guid as their id, while a legacy account's canonical
        // id is UuidV5(namespace, "{userId}|{upn}"). The two NEVER collide on id, so an accountId
        // seen-set alone cannot merge a pool/legacy pair for one mailbox. We therefore ALSO dedup by
        // normalized EMAIL (case-insensitive, trimmed): when a legacy account refers to the SAME
        // mailbox as a pool account already listed, the legacy entry is dropped — the pool entry wins
        // (its AccountRef = the pool accountId, which createPair/sync resolve directly). Edge case: a
        // legacy "default" account has no real email, so it is NEVER collapsed by email; it stays a
        // distinct entry. The accountId seen-set is still kept to guard against listing the SAME
        // legacy ref twice (a UPN whose canonical id already came from another legacy row).
        app.MapGet("/api/accounts", async (
            ICalendarAccountStore pool,
            IConnectedAccountStore legacy,
            ILegacyConnectedAccountAdapter adapter,
            CalendarAccountEmailBackfill backfill,
            CancellationToken ct) =>
        {
            var pooledRaw = await pool.ListAsync(ct);
            var legacyAccounts = await legacy.ListAsync(ct);

            // Best-effort one-time backfill: any Graph pool account whose email is still blank (it
            // was connected before /me capture existed) gets its real mailbox/name fetched and
            // persisted now, so AccountDisplayName resolves to the email and the UI never has to
            // fall back to the internal accountRef GUID. Already-named accounts pass through as-is.
            var pooled = new List<CalendarAccount>(pooledRaw.Count);
            foreach (var a in pooledRaw)
                pooled.Add(await backfill.EnsureEmailAsync(a, ct));

            var seen = new HashSet<string>(StringComparer.Ordinal);
            // Mailboxes already represented by a POOL account, normalized. A legacy account whose
            // mailbox is in here is the SAME casilla and is collapsed in favour of the pool entry.
            var pooledEmails = new HashSet<string>(StringComparer.Ordinal);
            var infos = new List<AccountInfo>();

            foreach (var account in pooled)
            {
                seen.Add(account.Id);
                var email = NormalizeMailbox(account.AccountEmail);
                if (email.Length > 0)
                    pooledEmails.Add(email);
                infos.Add(new AccountInfo
                {
                    AccountRef = account.Id,
                    DisplayName = AccountDisplayName(account),
                    Email = account.AccountEmail ?? "",
                    IsDefault = false,
                    // Surface the pool account's consent level ("read" | "readwrite") so the wizard can
                    // tell a read-only source from a read/write destination. Legacy accounts have no
                    // tracked scope and keep the record default ("").
                    Scope = account.Scope.ToString().ToLowerInvariant(),
                });
            }

            foreach (var account in legacyAccounts)
            {
                // Skip a legacy account whose canonical id already came from the pool.
                var canonicalId = await adapter.ResolveAccountIdAsync(account.UserPrincipalName, ct);
                if (!seen.Add(canonicalId))
                    continue;

                // Collapse the same casilla: a legacy account whose mailbox (its UPN) matches a pool
                // account already listed is dropped — the pool entry's AccountRef resolves it. A
                // legacy "default" account carries no real email, so LegacyMailbox returns empty and
                // it is never fused (a blank mailbox is not a match).
                var legacyEmail = LegacyMailbox(account.UserPrincipalName);
                if (legacyEmail.Length > 0 && pooledEmails.Contains(legacyEmail))
                    continue;

                infos.Add(new AccountInfo
                {
                    // Keep the legacy ref as-is (UPN / "default") so existing pairs and tests that
                    // reference the legacy account by UPN keep resolving unchanged.
                    AccountRef = account.UserPrincipalName,
                    DisplayName = string.Equals(account.UserPrincipalName, "default", StringComparison.Ordinal)
                        ? "Connected account"
                        : account.UserPrincipalName,
                    // A legacy account is keyed by its UPN, which IS the mailbox — except the
                    // "default" sentinel, which has no real email (LegacyMailbox returns empty).
                    Email = LegacyMailbox(account.UserPrincipalName),
                    IsDefault = false,
                });
            }

            // Default selection: a single account is the default; otherwise preserve the legacy
            // convention where the literal "default" UPN account is the implied default.
            if (infos.Count == 1)
            {
                infos[0] = infos[0] with { IsDefault = true };
            }
            else
            {
                for (var i = 0; i < infos.Count; i++)
                {
                    if (string.Equals(infos[i].AccountRef, "default", StringComparison.Ordinal))
                        infos[i] = infos[i] with { IsDefault = true };
                }
            }

            return Results.Ok(infos);
        }).RequireCookieOrIdentityBearer();

        app.MapDelete("/api/accounts/{accountRef}", async (
            string accountRef,
            ICalendarAccountStore pool,
            IConnectedAccountStore legacy,
            ISyncPairStore pairs,
            ILegacyConnectedAccountAdapter adapter,
            CancellationToken ct) =>
        {
            // Resolve through the adapter (pool-first, legacy fallback) so a pool accountId
            // (Guid) deletes just like a legacy UPN. Both adapter stores are user-scoped, so a
            // ref that belongs to another user (or to nobody) resolves to null. Return 404 (not
            // 403) so we never leak the existence of another user's account, and never run the
            // unlink cascade against it.
            if (await ResolveAccountForUserAsync(accountRef, adapter, ct) is null)
                return Results.NotFound();

            // Canonicalize the ref to the stable accountId so the pair-disable cascade matches
            // endpoints regardless of whether they carry the raw pool id or a legacy UPN.
            var accountId = await adapter.ResolveAccountIdAsync(accountRef, ct);

            // Track A-3 — DELETE every pair referencing this account (source or destination). The
            // destination calendar's events are intentionally left intact. Shared with the pool-delete
            // endpoint via the same canonical-id routine.
            var affectedPairIds = await CalendarConnectEndpoints
                .DeletePairsForAccountAsync(accountId, pairs, adapter, ct);

            // Remove from whichever store actually backs the account: a real pool account by its
            // canonical id, otherwise the legacy store by the raw UPN ref (as before).
            if (await pool.GetAsync(accountId, ct) is not null)
                await pool.RemoveAsync(accountId, ct);
            else
                await legacy.RemoveAsync(accountRef, ct);

            return Results.Ok(new { affectedPairIds });
        }).RequireCookieOrIdentityBearer();

        app.MapGet("/api/accounts/{accountRef}/calendars", async (
            string accountRef,
            ProviderRegistry registry,
            ILegacyConnectedAccountAdapter adapter,
            CancellationToken ct) =>
        {
            // Only enumerate calendars for an account the caller owns. Resolution goes through the
            // adapter (pool-first, legacy fallback) so a pool accountId resolves just like a legacy
            // UPN; both adapter stores are user-scoped, so a cross-user ref resolves to null -> 404
            // (don't leak existence, and don't hit Graph with another user's missing token, which
            // would 500). The resolved account's accountRef is passed to the Graph writer; its
            // token provider resolves the same accountId pool-first to fetch the right token.
            if (await ResolveAccountForUserAsync(accountRef, adapter, ct) is null)
                return Results.NotFound();

            var endpoint = new Endpoint
            {
                Provider = ProviderRegistry.MicrosoftGraph,
                AccountRef = accountRef,
                CalendarId = "",
            };
            var writer = registry.ResolveWriter(endpoint);
            var cals = await writer.ListCalendarsAsync();
            return Results.Ok(cals.Select(c => new
            {
                id = c.Id,
                displayName = c.DisplayName,
                isDefault = c.IsDefault,
                owner = c.Owner,
            }));
        }).RequireCookieOrIdentityBearer();

        // Create a new calendar in one of the caller's connected accounts. Mirrors the GET
        // above: resolve the account (pool-first, legacy fallback) so a cross-user / unknown
        // ref yields 404 (no existence leak, no Graph call with a foreign token), then delegate
        // to the same Graph writer. The destination of a sync can then be a fresh calendar.
        app.MapPost("/api/accounts/{accountRef}/calendars", async (
            string accountRef,
            CreateCalendarRequest req,
            ProviderRegistry registry,
            ILegacyConnectedAccountAdapter adapter,
            CancellationToken ct) =>
        {
            var validation = new CreateCalendarRequestValidator().Validate(req);
            if (!validation.IsValid)
                return Results.ValidationProblem(validation.ToDictionary());

            if (await ResolveAccountForUserAsync(accountRef, adapter, ct) is null)
                return Results.NotFound();

            var endpoint = new Endpoint
            {
                Provider = ProviderRegistry.MicrosoftGraph,
                AccountRef = accountRef,
                CalendarId = "",
            };
            var writer = registry.ResolveWriter(endpoint);
            var created = await writer.CreateCalendarAsync(req.Name!, ct);
            return Results.Ok(new
            {
                id = created.Id,
                displayName = created.DisplayName,
                isDefault = created.IsDefault,
                owner = created.Owner,
            });
        }).RequireCookieOrIdentityBearer();

        app.MapPost("/api/pairs", async (
            CreatePairRequest req,
            HttpContext http,
            ISyncPairStore store,
            ILegacyConnectedAccountAdapter adapter,
            IEntitlementsService entitlements,
            SyncBroadcaster broadcaster,
            ICurrentUserAccessor currentUser,
            CancellationToken ct) =>
        {
            var validation = new CreatePairRequestValidator().Validate(req);
            if (!validation.IsValid)
                return Results.ValidationProblem(validation.ToDictionary());

            // Plan gate. Resolve the caller's EFFECTIVE entitlements (plan defaults intersected with
            // their toggles). Today every cap is unlocked (MaxPairs = int.MaxValue, MinSyncInterval = 1),
            // so this changes NO behaviour now — it wires the gate so a future PlanBasedEntitlementsService
            // (one DI line) enforces real caps without touching this endpoint. The pair store is
            // user-scoped, so ListAsync() already returns only the caller's pairs; we count the ones that
            // still occupy a plan slot (anything not "disabled") and reject when the user is at the cap.
            var effective = await entitlements.GetForUserAsync(currentUser.UserId, ct).ConfigureAwait(false);
            var existingPairs = await store.ListAsync(ct).ConfigureAwait(false);
            var activeCount = existingPairs.Count(p => !string.Equals(p.State, "disabled", StringComparison.Ordinal));
            if (activeCount >= effective.MaxPairs)
            {
                return Results.Json(
                    new { error = "plan_limit_reached", message = "You have reached the maximum number of sync pairs for your plan." },
                    statusCode: StatusCodes.Status402PaymentRequired);
            }

            // Clamp the requested interval up to the plan's floor (MinSyncIntervalMinutes). Today the
            // floor is 1 (no clamp); under a paid floor a request below it is raised to the floor rather
            // than rejected, so creating a pair never hard-fails on interval alone.
            var intervalMin = Math.Max(req.IntervalMin, effective.MinSyncIntervalMinutes);

            // Validate that any referenced connected account belongs to the current user.
            // Resolution goes through the adapter (pool-first, legacy fallback), so an explicit
            // AccountRef is accepted when it resolves to EITHER a pool accountId OR a legacy UPN
            // the user owns; both adapter stores are user-scoped, so a foreign/nonexistent ref
            // resolves to null. We only check Graph endpoints with an explicit AccountRef:
            // OutlookCom endpoints have no server-side account, and a null/empty ref normalizes
            // to the user's "default" account. A clean 400 here is friendlier than a later token
            // failure.
            if (await ReferencedAccountIsMissingAsync(req.Source!, adapter, ct))
                return Results.BadRequest(new { error = "unknown_source_account", message = "source.accountRef does not belong to the current user." });
            if (await ReferencedAccountIsMissingAsync(req.Destination!, adapter, ct))
                return Results.BadRequest(new { error = "unknown_destination_account", message = "destination.accountRef does not belong to the current user." });

            // §B-4 — a pair must mirror BETWEEN two distinct calendars. Reject source == destination
            // so a run can never sweep the calendar it is reading from. Sameness is compared on the
            // canonical accountId: two refs of the SAME representation for one account collapse
            // (a legacy UPN resolves to its derived id, a pool ref to itself), plus the calendar id.
            // §A-2 — the cross-representation self-mirror IS now covered: a pool account uses a fresh
            // Guid (so the same mailbox connected BOTH as legacy AND as a pool account does not
            // collapse on accountId), so IsSameSourceAndDestinationAsync ALSO compares the resolved
            // MAILBOX email (case-insensitive) + calendar id as an extra net. For two OutlookCom
            // (COM) endpoints there is no server account, so we compare AccountRef + calendar id.
            if (await IsSameSourceAndDestinationAsync(req.Source!, req.Destination!, adapter, ct))
                return Results.BadRequest(new { error = "same_source_destination", message = "source and destination must be different calendars." });

            var pair = new SyncPair
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = req.Name!,
                Source = req.Source!,
                Destination = req.Destination!,
                IntervalMin = intervalMin,
                State = "active",
            };

            // Track B — pin the pair to the creating device when it has a COM side and the caller
            // supplied its deviceId. A pin on a non-COM pair is ignored (there is no COM source to
            // own); a blank pin is normalized to null. A COM pair created without a pin is claimed on
            // its first /push instead (claim-on-first-push), so back-compat is preserved either way.
            if (IsComPinnedPair(pair) && !string.IsNullOrWhiteSpace(req.PinnedDeviceId))
                pair = pair with { PinnedDeviceId = req.PinnedDeviceId };

            await store.AddAsync(pair);

            // A new row appeared in the user's pair set: tell the user's OTHER live sessions to reload
            // /api/pairs so a second open window/machine shows the new pair without re-opening Calendar.
            // The creator is a cookie/identity-bearer caller with no deviceId claim, so the origin is
            // empty and the reload reaches all sessions (a redundant reload on the creator is harmless).
            var createDeviceId = http.User.FindFirst("deviceId")?.Value ?? string.Empty;
            await broadcaster.BroadcastPairsChangedAsync(currentUser.UserId, createDeviceId, ct).ConfigureAwait(false);

            return Results.Ok(pair);
        }).RequireCookieOrIdentityBearer();

        app.MapGet("/api/pairs", async (ISyncPairStore store, DeviceService devices, CancellationToken ct) =>
        {
            var pairs = await store.ListAsync(ct);
            // Resolve the pinned-device name + online flag for COM-pinned pairs with ONE device list
            // read (not one per pair). Both stores are user-scoped, so only the caller's devices are
            // visible — a pin to a device the caller cannot see resolves to "unknown / offline".
            var deviceMap = await BuildDeviceMapAsync(pairs, devices, ct);
            return Results.Ok(pairs.Select(p => EnrichPair(p, deviceMap)).ToList());
        }).RequireCookieOrIdentityBearer();

        app.MapGet("/api/pairs/{id}", async (string id, ISyncPairStore store, DeviceService devices, CancellationToken ct) =>
        {
            // The store filters by current user, so a pair owned by another user (or absent)
            // resolves to null -> 404. Never 403: don't reveal that the id exists elsewhere.
            var pair = await store.GetAsync(id, ct);
            if (pair is null)
                return Results.NotFound();
            var deviceMap = await BuildDeviceMapAsync(new[] { pair }, devices, ct);
            return Results.Ok(EnrichPair(pair, deviceMap));
        }).RequireCookieOrIdentityBearer();

        app.MapPatch("/api/pairs/{id}", async (
            string id,
            UpdatePairRequest req,
            HttpContext http,
            ISyncPairStore store,
            ISyncRunLock runLock,
            ILegacyConnectedAccountAdapter adapter,
            SyncBroadcaster broadcaster,
            ICurrentUserAccessor currentUser,
            Microsoft.Extensions.Options.IOptions<ServerOptions> opts,
            CancellationToken ct) =>
        {
            var validation = new UpdatePairRequestValidator().Validate(req);
            if (!validation.IsValid)
                return Results.ValidationProblem(validation.ToDictionary());

            // FIX D — serialize the PATCH read-modify-write with the per-pair run-lock. A /run or
            // /push persists the WHOLE pair row (RecordRunAsync -> store.UpdateAsync rewrites every
            // column), so a re-target PATCH racing a run could be silently clobbered — the new
            // destination (and any future per-column queue) lost, orphaning events in the old
            // destination forever. Holding the lock here makes PATCH and run mutually exclusive: if a
            // run is in progress we answer 409 run_in_progress (the client retries) instead of
            // racing. The lock is fast to hold (a synchronous read-modify-write, no Graph I/O), so it
            // never wedges a run, and acquiring it before GetAsync guarantees we read the latest row.
            await using var handle = await runLock
                .TryAcquireAsync(id, LockTtl(opts.Value), owner: "patch", ct)
                .ConfigureAwait(false);
            if (handle is null)
                return RunLockBusy();

            var existing = await store.GetAsync(id, ct);
            if (existing is null)
                return Results.NotFound();

            // §F2 — endpoint edits. The EFFECTIVE source/destination is the incoming one when
            // supplied, else the existing side. Apply the SAME account-ownership + origin!=dest
            // validation createPair uses, so editing can never point a pair at a foreign account
            // or self-mirror a calendar into itself.
            var effectiveSource = req.Source ?? existing.Source;
            var effectiveDestination = req.Destination ?? existing.Destination;

            if (req.Source is not null || req.Destination is not null)
            {
                if (await ReferencedAccountIsMissingAsync(effectiveSource, adapter, ct))
                    return Results.BadRequest(new { error = "unknown_source_account", message = "source.accountRef does not belong to the current user." });
                if (await ReferencedAccountIsMissingAsync(effectiveDestination, adapter, ct))
                    return Results.BadRequest(new { error = "unknown_destination_account", message = "destination.accountRef does not belong to the current user." });
                if (await IsSameSourceAndDestinationAsync(effectiveSource, effectiveDestination, adapter, ct))
                    return Results.BadRequest(new { error = "same_source_destination", message = "source and destination must be different calendars." });
            }

            // A change to either endpoint means the destructive mirror's reconciliation set
            // changes: a new destination has no events carrying this source's CalImportSourceId
            // (so the next run starts fresh), and a new source changes the id set the kept
            // destination is reconciled against. There is no per-pair sync state on the server
            // (ISyncStateStore is the device-push path, keyed by deviceId), so "reset the pair's
            // state" reduces to clearing the visual run counters — the next run re-mirrors fresh.
            var endpointChanged =
                !EndpointsEqual(effectiveSource, existing.Source) ||
                !EndpointsEqual(effectiveDestination, existing.Destination);

            // FIX 3 — when the DESTINATION changes, the OLD destination still holds the events this
            // pair created (CalImportPairId == pair.Id). The opt-in /cleanup-destination call is the
            // immediate path, but if the client never makes it (crash/close) those events would be
            // orphaned forever. So record the old destination as a pending cleanup; the next run/push
            // drains it idempotently server-side. Only Graph destinations carry server-managed events
            // (OutlookCom has none), and we never enqueue the NEW current destination (it must not be
            // swept). De-dup so repeated re-targets don't pile duplicate entries.
            var destinationChanged = !EndpointsEqual(effectiveDestination, existing.Destination);
            var pendingCleanup = existing.PendingCleanupDestinations is { Count: > 0 }
                ? new List<Endpoint>(existing.PendingCleanupDestinations)
                : new List<Endpoint>();
            if (destinationChanged
                && string.Equals(existing.Destination.Provider, ProviderRegistry.MicrosoftGraph, StringComparison.Ordinal))
            {
                EnqueuePendingCleanup(pendingCleanup, existing.Destination, effectiveDestination);
            }

            var updated = existing with
            {
                Name = req.Name ?? existing.Name,
                IntervalMin = req.IntervalMin ?? existing.IntervalMin,
                State = req.State ?? existing.State,
                Source = effectiveSource,
                Destination = effectiveDestination,
                LastRunUtc = endpointChanged ? null : existing.LastRunUtc,
                LastResult = endpointChanged ? null : existing.LastResult,
                PendingCleanupDestinations = pendingCleanup,
            };
            await store.UpdateAsync(updated, ct);

            // A pair changed (renamed, re-targeted, paused/resumed): tell the user's OTHER live sessions
            // to reload /api/pairs so a second open window/machine reflects the edit live. Cookie/
            // identity-bearer caller has no deviceId claim, so origin is empty and the reload reaches all
            // sessions (a redundant reload on the editing session is harmless).
            var patchDeviceId = http.User.FindFirst("deviceId")?.Value ?? string.Empty;
            await broadcaster.BroadcastPairsChangedAsync(currentUser.UserId, patchDeviceId, ct).ConfigureAwait(false);

            return Results.Ok(updated);
        }).RequireCookieOrIdentityBearer();

        // §F1 (Graph source) — export the pair's SOURCE calendar for one month as a Simple-mode
        // .txt, byte-identical to CalExport's Simple output. Only Graph sources have a server-side
        // reader; an OutlookCom source must export locally via COM (the App's generateTxt path), so
        // it gets a 409 no_server_reader here. The pair is user-scoped (404 when not the caller's).
        app.MapPost("/api/pairs/{id}/export-source-txt", async (
            string id,
            ExportSourceTxtRequest req,
            ISyncPairStore store,
            ProviderRegistry registry,
            CancellationToken ct) =>
        {
            ArgumentNullException.ThrowIfNull(req);

            if (req.Month is < 1 or > 12)
                return Results.BadRequest(new { error = "invalid_month", message = "Month must be between 1 and 12." });
            if (req.Year is < 1 or > 9999)
                return Results.BadRequest(new { error = "invalid_year", message = "Year is out of range." });

            // The pair is user-scoped: GetAsync filters by the current user, so a pair the caller
            // does not own (or an unknown id) resolves to null -> 404, and the export never reads
            // another user's source calendar via Graph. CrossUserIsolationTests proves this end to
            // end (a signed-in user B exporting user A's pair id gets 404 and triggers no read).
            var pair = await store.GetAsync(id, ct);
            if (pair is null)
                return Results.NotFound();

            // OutlookCom has no server reader: its events live in local Outlook, read on the
            // device via COM. The App routes that case through generateTxt instead.
            var reader = registry.ResolveReader(pair.Source);
            if (reader is null)
                return Results.Conflict(new { error = "no_server_reader", message = "This source has no server reader; export it locally via Outlook." });

            // The .txt must show each event's LOCAL clock time (what the user sees in their
            // calendar), consistent with CalExport's COM exporter — NOT UTC. So the reader runs in
            // preserveLocalTime mode (no Prefer:UTC), and "June" must mean the USER'S June, not a
            // UTC June. We do not know the mailbox's zone server-side, and each event carries its
            // OWN declared zone, so we render every event in its own local zone and keep only those
            // whose LOCAL date falls in the requested month/year. To guarantee no boundary event is
            // missed across any earth offset (UTC-12..UTC+14), the Graph window is padded one day on
            // each side; the precise month filter below trims it back to exactly the user's month.
            var monthStart = new DateTime(req.Year, req.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
            var monthEnd = monthStart.AddMonths(1);
            var from = new DateTimeOffset(monthStart, TimeSpan.Zero).AddDays(-1);
            var to = new DateTimeOffset(monthEnd, TimeSpan.Zero).AddDays(1);

            IReadOnlyList<AppointmentRecord> records;
            try
            {
                records = await reader
                    .ReadWindowAsync(pair.Source.CalendarId, from, to, ct, preserveLocalTime: true)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransientReadFailure(ex))
            {
                // A transient Graph blip (429/timeout/network) is retryable: surface 503 so the
                // client can offer "try again" instead of a hard failure.
                return Results.Json(
                    new { error = "transient_read_failure", message = "The calendar could not be read right now; try again shortly." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            // Keep only events whose LOCAL start date lands in the requested month. Start now
            // carries local wall-clock time (preserveLocalTime), so this is the user's month.
            records = records
                .Where(r => r.Start >= monthStart && r.Start < monthEnd)
                .ToList();

            if (!req.IncludeCancelled)
                records = records.Where(r => !r.IsCancelled).ToList();

            var txt = SimpleAppointmentFormatter.Format(records);
            return Results.Text(txt, "text/plain");
        }).RequireCookieOrIdentityBearer();

        // §F2-cleanup — count the events THIS pair created in a candidate destination, without
        // deleting anything. Drives the edit wizard's "Also remove the N events already copied to
        // the previous destination" confirm. User-scoped (404 for a pair the caller does not own);
        // the destination must belong to the caller too. A non-Graph destination has no managed
        // events the server can enumerate, so it reports 0.
        app.MapGet("/api/pairs/{id}/managed-count", async (
            string id,
            string? provider,
            string? accountRef,
            string? calendarId,
            ISyncPairStore store,
            ProviderRegistry registry,
            ILegacyConnectedAccountAdapter adapter,
            CancellationToken ct) =>
        {
            var pair = await store.GetAsync(id, ct);
            if (pair is null)
                return Results.NotFound();

            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(calendarId))
                return Results.BadRequest(new { error = "invalid_destination", message = "provider and calendarId are required." });

            var destination = new Endpoint
            {
                Provider = provider!,
                AccountRef = string.IsNullOrWhiteSpace(accountRef) ? null : accountRef,
                CalendarId = calendarId!,
            };

            // The destination must belong to the caller — never enumerate a foreign account.
            if (await ReferencedAccountIsMissingAsync(destination, adapter, ct))
                return Results.BadRequest(new { error = "unknown_destination_account", message = "destination.accountRef does not belong to the current user." });

            // OutlookCom destinations have no server-side managed-event enumeration.
            if (!string.Equals(destination.Provider, ProviderRegistry.MicrosoftGraph, StringComparison.Ordinal))
                return Results.Ok(new { count = 0 });

            var writer = registry.ResolveWriter(destination);
            int count;
            try
            {
                count = await writer.CountManagedAsync(destination.CalendarId, pair.Id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransientReadFailure(ex))
            {
                return Results.Json(
                    new { error = "transient_read_failure", message = "The calendar could not be read right now; try again shortly." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            return Results.Ok(new { count });
        }).RequireCookieOrIdentityBearer();

        // §F2-cleanup — delete from the PREVIOUS destination ONLY the events this pair created
        // (CalImportPairId == pair.Id). Opt-in, called by the App AFTER a successful PATCH that
        // re-targeted the pair, so a failed re-target never triggers a destructive cleanup.
        //
        // Safety invariants enforced here:
        //   1) user-scoped: GetAsync(id) 404s for a pair the caller does not own;
        //   2) the destination to clean must belong to the caller (ReferencedAccountIsMissingAsync);
        //   3) it must NOT be the pair's CURRENT destination (never delete what was just written);
        //   4) the per-pair run-lock is held so cleanup never races a concurrent sync of the pair;
        //   5) only events carrying CalImportPairId == pair.Id are enumerated/deleted — the user's
        //      own events and other pairs' events are never touched (guaranteed in the Graph layer).
        app.MapPost("/api/pairs/{id}/cleanup-destination", async (
            string id,
            CleanupDestinationRequest req,
            ISyncPairStore store,
            ISyncRunLock runLock,
            ProviderRegistry registry,
            ILegacyConnectedAccountAdapter adapter,
            Microsoft.Extensions.Options.IOptions<ServerOptions> opts,
            CancellationToken ct) =>
        {
            ArgumentNullException.ThrowIfNull(req);

            var validation = new CleanupDestinationRequestValidator().Validate(req);
            if (!validation.IsValid)
                return Results.ValidationProblem(validation.ToDictionary());

            var pair = await store.GetAsync(id, ct);
            if (pair is null)
                return Results.NotFound();

            var destination = req.Destination!;

            if (await ReferencedAccountIsMissingAsync(destination, adapter, ct))
                return Results.BadRequest(new { error = "unknown_destination_account", message = "destination.accountRef does not belong to the current user." });

            // Refuse to clean the pair's CURRENT destination: that would delete the events the most
            // recent (or in-flight) sync just wrote. Cleanup is for the OLD destination only.
            // Canonicalize both account refs (legacy UPN vs pool accountId, plus the cross-rep mailbox
            // net) so the SAME mailbox referenced two different ways is never mistaken for a different
            // destination — a raw Ordinal AccountRef compare would let the current destination through.
            if (await EndpointsAddressSameCalendarAsync(destination, pair.Destination, adapter, ct))
                return Results.BadRequest(new { error = "destination_is_current", message = "Refusing to clean the pair's current destination." });

            // A non-Graph (OutlookCom) destination has no server-side managed events to remove.
            if (!string.Equals(destination.Provider, ProviderRegistry.MicrosoftGraph, StringComparison.Ordinal))
                return Results.Ok(new { deleted = 0, failures = Array.Empty<string>() });

            // Hold the per-pair run-lock so the destructive cleanup never overlaps a sync of the
            // same pair (which could be writing to the new destination at the same time).
            await using var handle = await runLock
                .TryAcquireAsync(id, LockTtl(opts.Value), owner: "cleanup", ct)
                .ConfigureAwait(false);
            if (handle is null)
                return RunLockBusy();

            var writer = registry.ResolveWriter(destination);
            CleanupResult result;
            try
            {
                result = await writer.CleanupManagedAsync(destination.CalendarId, pair.Id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransientReadFailure(ex))
            {
                return Results.Json(
                    new { error = "transient_read_failure", message = "The calendar could not be read right now; try again shortly." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            // FIX 3 — the opt-in cleanup is the immediate path; once it succeeds with no failures the
            // server-side pending-cleanup entry for this destination is satisfied and is dequeued so a
            // later run does not re-enumerate it. A partial cleanup (some deletes failed) leaves the
            // entry queued so the eventual server-side drain finishes it.
            if (result.Failures.Count == 0
                && pair.PendingCleanupDestinations is { Count: > 0 })
            {
                var trimmed = pair.PendingCleanupDestinations
                    .Where(e => !EndpointsEqual(e, destination))
                    .ToList();
                if (trimmed.Count != pair.PendingCleanupDestinations.Count)
                    await store.UpdateAsync(pair with { PendingCleanupDestinations = trimmed }, ct).ConfigureAwait(false);
            }

            return Results.Ok(new { deleted = result.Deleted, failures = result.Failures });
        }).RequireCookieOrIdentityBearer();

        app.MapDelete("/api/pairs/{id}", async (
            string id,
            HttpContext http,
            ISyncPairStore store,
            SyncBroadcaster broadcaster,
            ICurrentUserAccessor currentUser,
            CancellationToken ct) =>
        {
            // Confirm ownership before deleting: a cross-user (or absent) id resolves to null
            // in the user-scoped store -> 404, so RemoveAsync never silently no-ops on a pair
            // the caller doesn't own and we don't leak its existence.
            if (await store.GetAsync(id, ct) is null)
                return Results.NotFound();

            await store.RemoveAsync(id, ct);

            // A row disappeared from the user's pair set: tell the user's OTHER live sessions to reload
            // /api/pairs so a second open window/machine drops the deleted pair live. Cookie/identity-
            // bearer caller has no deviceId claim, so origin is empty and the reload reaches all sessions.
            var deleteDeviceId = http.User.FindFirst("deviceId")?.Value ?? string.Empty;
            await broadcaster.BroadcastPairsChangedAsync(currentUser.UserId, deleteDeviceId, ct).ConfigureAwait(false);

            return Results.NoContent();
        }).RequireCookieOrIdentityBearer();

        app.MapPost("/api/pairs/{id}/push", async (
            string id,
            PushRequest req,
            HttpContext http,
            ISyncPairStore store,
            ISyncRunLock runLock,
            ProviderRegistry registry,
            DeviceService devices,
            SyncBroadcaster broadcaster,
            ICurrentUserAccessor currentUser,
            Microsoft.Extensions.Options.IOptions<ServerOptions> opts,
            CancellationToken ct) =>
        {
            ArgumentNullException.ThrowIfNull(req);

            var pair = await store.GetAsync(id, ct);
            if (pair is null)
                return Results.NotFound();

            // FIX C — a /push is the App's clearest "I am running and syncing" signal, so renew the
            // calling device's lease (LeaseUntil + LastSeenUtc) here. Without this the lease set at
            // register expires after DeviceLeaseTtlMinutes and the cron runner's `covered` set is
            // always empty, so cron would double-run the user's pairs alongside the active App. Only
            // an api-key (device) caller carries a deviceId claim; a cookie/identity-bearer caller
            // (a human, not a running App) does not, so its push correctly does not extend any lease.
            var deviceId = http.User.FindFirst("deviceId")?.Value;
            if (!string.IsNullOrWhiteSpace(deviceId))
                await devices.HeartbeatAsync(deviceId, ct).ConfigureAwait(false);

            // Track B (claim-on-first-push) — a COM-pinned pair that has no pinned device yet (created
            // before COM-pinning existed, or by a human createPair with no deviceId) is claimed by the
            // FIRST device that pushes it. This makes pre-existing COM pairs converge onto a single
            // owning device without a migration backfill: the device actually reading that Outlook is
            // the one pushing, so it is the correct pin. Idempotent — once pinned, a different device's
            // push leaves the pin untouched here (the run-lock + ordering still serialize the mirror;
            // re-pinning would be a deliberate UI action, not a side effect of a push).
            if (string.IsNullOrWhiteSpace(pair.PinnedDeviceId)
                && !string.IsNullOrWhiteSpace(deviceId)
                && IsComPinnedPair(pair))
            {
                pair = pair with { PinnedDeviceId = deviceId };
                await store.UpdateAsync(pair, ct).ConfigureAwait(false);
            }

            // Track B — reject a destructive mirror from the WRONG device. Once a COM-pinned pair has
            // an owning device, only that device may push it: a push from any other device would mirror
            // that device's Outlook view onto the destination, clobbering the owner's events. Reject
            // with 409 pinned_to_other_device BEFORE the run-lock and the mirror so nothing is mutated.
            // A blank pin is the claim-on-first-push case handled above and is intentionally allowed.
            if (IsComPinnedPair(pair)
                && !string.IsNullOrWhiteSpace(pair.PinnedDeviceId)
                && !string.Equals(pair.PinnedDeviceId, deviceId, StringComparison.Ordinal))
            {
                return Results.Conflict(new
                {
                    error = "pinned_to_other_device",
                    message = "This pair's Outlook source is pinned to a different device; it can only be pushed from there.",
                });
            }

            // §B-1 — acquire the per-pair run lock INSIDE the endpoint before the destructive
            // mirror. If another executor (manual run, overlapping tick) holds it, skip with
            // 409 instead of running a second concurrent sweep against the same calendar.
            await using var handle = await runLock
                .TryAcquireAsync(id, LockTtl(opts.Value), owner: "push", ct)
                .ConfigureAwait(false);
            if (handle is null)
                return RunLockBusy();

            // FIX 3 — drain any old destinations queued by a prior re-target BEFORE mirroring, so a
            // client that never called /cleanup-destination still has its orphans removed eventually.
            // The lock is held, so this never races another run/cleanup of the same pair.
            var drained = await DrainPendingCleanupAsync(pair, registry, ct).ConfigureAwait(false);
            pair = pair with { PendingCleanupDestinations = drained };

            var writer = registry.ResolveWriter(pair.Destination);
            var (from, to) = Window(opts.Value);
            // FIX 2 — renew the run-lock while the (potentially long) destructive mirror runs, so the
            // lock cannot expire mid-mirror and let a second executor start a concurrent sweep.
            var mirrorPair = pair;
            var result = await SyncRunLockHeartbeat.RunAsync(
                handle, LockTtl(opts.Value),
                token => writer.MirrorAsync(
                    mirrorPair.Destination.CalendarId, req.Events, ReminderMinutes, from, to, token, mirrorPair.Id),
                ct).ConfigureAwait(false);

            // Exclude the pushing device from the fan-out (it already has the result). A cookie/
            // identity-bearer human has no deviceId claim, so originDeviceId is empty and the run
            // reaches every one of the user's live sessions.
            await RecordRunAsync(
                store, pair, result, ct,
                broadcaster, currentUser.UserId, deviceId ?? string.Empty).ConfigureAwait(false);
            return Results.Ok(result);
        }).RequireCookieOrApiKeyOrIdentityBearer();

        // Track B — sync-now signal for a COM-pinned pair. A pair whose source is read via Outlook COM
        // can only sync on the ONE device that owns that Outlook (its pinnedDeviceId). When a caller
        // that is NOT that device asks to sync now, the server stamps SyncRequestedUtc; the pinned
        // device's scheduler sees the newer value and runs the pair on its next tick. Auth mirrors
        // /push (cookie OR api-key OR identity-bearer) so it serves both a human in the panel and a
        // second device. Outcomes:
        //   404                       — pair not the caller's (user-scoped store).
        //   409 not_com_pinned        — the pair has no COM side; sync it directly via /run instead.
        //   200 {status:"local"}      — the CALLER is the pinned device; it should run locally, not
        //                               request a signal (the App routes this case to a local run).
        //   200 {status:"origin_unavailable", device} — the pinned device's lease is dead (App not
        //                               running), so nothing can pick up the signal right now.
        //   200 {status:"requested", device}          — signal stamped; the pinned device will run it.
        app.MapPost("/api/pairs/{id}/request-sync", async (
            string id,
            HttpContext http,
            ISyncPairStore store,
            DeviceService devices,
            CancellationToken ct) =>
        {
            var pair = await store.GetAsync(id, ct);
            if (pair is null)
                return Results.NotFound();

            // Only COM-pinned pairs use the signal path. A Graph<->Graph pair has a server reader and
            // is run directly via /run, so asking it to "request sync" is a client error (409), not a
            // silent no-op that would confuse the caller into thinking a run was queued.
            if (!IsComPinnedPair(pair))
                return Results.Conflict(new
                {
                    error = "not_com_pinned",
                    message = "This pair has no Outlook COM side; run it directly instead of requesting a device sync.",
                });

            // A COM pair with no pin yet has no device to signal. Treat it as origin-unavailable so the
            // UI shows the same "can't reach the origin device" state rather than a misleading success;
            // the first push from the owning device will claim the pin (claim-on-first-push).
            if (string.IsNullOrWhiteSpace(pair.PinnedDeviceId))
                return Results.Ok(new { status = "origin_unavailable", device = (string?)null });

            var pinnedDevice = await devices.GetByIdAsync(pair.PinnedDeviceId!, ct);
            var deviceName = pinnedDevice?.Name;

            // The caller IS the pinned device (an api-key push principal carries its deviceId). It must
            // run the pair LOCALLY rather than stamping a signal it would then service itself, so report
            // "local" and let the App run in place.
            var callerDeviceId = http.User.FindFirst("deviceId")?.Value;
            if (!string.IsNullOrWhiteSpace(callerDeviceId)
                && string.Equals(callerDeviceId, pair.PinnedDeviceId, StringComparison.Ordinal))
                return Results.Ok(new { status = "local", device = deviceName });

            // Origin must be online (live lease) to pick the signal up. A dead lease means the App is
            // not running on the pinned device, so we report origin_unavailable instead of stamping a
            // signal nobody will service.
            if (!await devices.IsDeviceOnlineAsync(pair.PinnedDeviceId!, ct))
                return Results.Ok(new { status = "origin_unavailable", device = deviceName });

            // Stamp the signal. No run-lock needed: this only advances SyncRequestedUtc; a concurrent
            // run merely clears or re-stamps it, neither of which is destructive.
            await store.UpdateAsync(pair with { SyncRequestedUtc = DateTimeOffset.UtcNow }, ct);
            return Results.Ok(new { status = "requested", device = deviceName });
        }).RequireCookieOrApiKeyOrIdentityBearer();

        app.MapPost("/api/pairs/{id}/run", async (
            string id,
            HttpContext http,
            ISyncPairStore store,
            ISyncRunLock runLock,
            SyncModuleRegistry modules,
            ProviderRegistry registry,
            SyncBroadcaster broadcaster,
            ICurrentUserAccessor currentUser,
            Microsoft.Extensions.Options.IOptions<ServerOptions> opts,
            CancellationToken ct) =>
        {
            var pair = await store.GetAsync(id, ct);
            if (pair is null)
                return Results.NotFound();

            // The pair's kind is implicitly "calendar" today; resolve its module from the
            // registry. A pair whose module is unknown (no module registered for its kind)
            // cannot run — only calendar exists for now.
            var module = modules.GetCalendar();
            if (module is null)
                return NoModule();

            // §B-1 — acquire the per-pair run lock before the read+mirror so a manual run and
            // a background tick (or two ticks) never run the same destructive mirror at once.
            // The lock WRAPS the module call; it is never taken inside the module.
            await using var handle = await runLock
                .TryAcquireAsync(id, LockTtl(opts.Value), owner: "run", ct)
                .ConfigureAwait(false);
            if (handle is null)
                return RunLockBusy();

            // FIX 3 — drain any old destinations queued by a prior re-target BEFORE the mirror, so a
            // client that never called /cleanup-destination still has its orphans removed eventually.
            // The lock is held, so this never races another run/cleanup of the same pair.
            var drained = await DrainPendingCleanupAsync(pair, registry, ct).ConfigureAwait(false);
            pair = pair with { PendingCleanupDestinations = drained };

            var (from, to) = Window(opts.Value);

            // Delegate the read + destructive mirror to the calendar module. The §A-3 transient
            // read guard and the conditional window sweep live inside the module / CalendarMirror.
            // FIX 2 — wrapped in the run-lock heartbeat so a long read+mirror cannot let the lock
            // expire mid-run and admit a concurrent destructive sweep.
            var runPair = pair;
            var outcome = await SyncRunLockHeartbeat.RunAsync(
                handle, LockTtl(opts.Value),
                token => module.ExecuteAsync(runPair, from, to, token),
                ct).ConfigureAwait(false);
            if (outcome.NoServerReader)
            {
                // OutlookCom sources have no server-side read; their events arrive via /push.
                return Results.Conflict(new
                {
                    error = "no_server_reader",
                    message = "This source provider has no server reader; use the push endpoint.",
                });
            }

            // Fan the run out to the user's OTHER live sessions. A /run is a server-side read+mirror
            // (Graph source); the calling principal is a human (cookie) or a device (api-key) — exclude
            // a device caller via its deviceId claim, empty for a human.
            var runDeviceId = http.User.FindFirst("deviceId")?.Value ?? string.Empty;
            await RecordRunAsync(
                store, pair, outcome.Result!, ct,
                broadcaster, currentUser.UserId, runDeviceId).ConfigureAwait(false);
            return Results.Ok(outcome.Result);
        }).RequireCookieOrApiKey();
    }

    // FIX 3 — append `oldDestination` to the pending-cleanup list unless it is the same calendar as
    // the NEW destination (never schedule a drain of the destination the pair now writes to) or is
    // already queued. Identity is compared on the raw endpoint triple (provider/accountRef/calendar);
    // the drain itself is keyed by pair.Id in the Graph layer so even a near-duplicate entry is a
    // harmless no-op re-enumeration.
    private static void EnqueuePendingCleanup(List<Endpoint> pending, Endpoint oldDestination, Endpoint newDestination)
    {
        if (EndpointsEqual(oldDestination, newDestination))
            return;
        if (pending.Any(e => EndpointsEqual(e, oldDestination)))
            return;
        pending.Add(oldDestination);
    }

    // FIX 3 — drain the pair's pending cleanup destinations idempotently at the start of a run/push.
    // Each entry is a destination this pair previously wrote to and was re-targeted away from; the
    // Graph cleanup deletes ONLY the events carrying CalImportPairId == pair.Id, so re-running a
    // partially-completed drain only re-deletes what is still present. An entry that the pair now
    // writes to again (it equals the CURRENT destination) is dropped WITHOUT deleting — those events
    // are live again. A transient failure leaves the entry queued for the next run; a fully drained
    // entry (no failures) is removed. Returns the updated pending list to persist.
    //
    // NOTE: the caller holds the per-pair run lock, so this never races a concurrent sync/cleanup of
    // the same pair. It runs BEFORE the mirror so a re-targeted pair's old events are gone before new
    // ones are written.
    private static async Task<List<Endpoint>> DrainPendingCleanupAsync(
        SyncPair pair, ProviderRegistry registry, CancellationToken ct)
    {
        if (pair.PendingCleanupDestinations is not { Count: > 0 })
            return pair.PendingCleanupDestinations ?? new List<Endpoint>();

        var remaining = new List<Endpoint>();
        foreach (var dest in pair.PendingCleanupDestinations)
        {
            // Never clean the pair's CURRENT destination (events there are live again) and never
            // clean a non-Graph endpoint (no server-managed events to remove): drop it silently.
            if (EndpointsEqual(dest, pair.Destination)
                || !string.Equals(dest.Provider, ProviderRegistry.MicrosoftGraph, StringComparison.Ordinal))
                continue;

            try
            {
                var writer = registry.ResolveWriter(dest);
                var result = await writer.CleanupManagedAsync(dest.CalendarId, pair.Id, ct).ConfigureAwait(false);
                // Any failure (transient or otherwise) means some managed events may remain; keep the
                // entry queued so the next run re-enumerates and finishes the drain.
                if (result.Failures.Count > 0)
                    remaining.Add(dest);
            }
            catch
            {
                // A hard failure (e.g. token blip, transport drop) must not abort the whole run; keep
                // the entry queued and let the next run retry. The mirror still proceeds.
                remaining.Add(dest);
            }
        }

        return remaining;
    }

    // Endpoint identity for the §F2 "did this side change?" check: a change to the provider,
    // account or calendar id means a different reconciliation set, so the run counters reset.
    // CalendarName is a display label only and is intentionally NOT compared.
    // True when the pair's SOURCE is an OutlookCom (COM) endpoint, so the pair can only sync through
    // a device's /push and is the one a pinnedDeviceId applies to. In practice the COM side is ALWAYS
    // the source: there is no COM writer (the destination is always Graph), so a destination can never
    // be OutlookCom. The detection rule is shared verbatim across the three executors — this method,
    // CronSyncRunner.IsComPinned (raw row) and PairRunner.IsOutlookCom (engine) — and ALL THREE use
    // source-only with OrdinalIgnoreCase. They must agree exactly or a pair could be run by two paths.
    internal static bool IsComPinnedPair(SyncPair pair) =>
        string.Equals(pair.Source.Provider, ProviderRegistry.OutlookCom, StringComparison.OrdinalIgnoreCase);

    private static bool EndpointsEqual(Endpoint a, Endpoint b) =>
        string.Equals(a.Provider, b.Provider, StringComparison.Ordinal)
        && string.Equals(a.AccountRef ?? "", b.AccountRef ?? "", StringComparison.Ordinal)
        && string.Equals(a.CalendarId, b.CalendarId, StringComparison.Ordinal);

    private const int ReminderMinutes = 30;

    private static TimeSpan LockTtl(ServerOptions opts)
        => TimeSpan.FromMinutes(opts.SyncRunLockTtlMinutes <= 0 ? 8 : opts.SyncRunLockTtlMinutes);

    // 409: another executor is already running this pair. Distinct error code so the client
    // can tell "busy, try later" apart from the no_server_reader 409.
    private static IResult RunLockBusy()
        => Results.Conflict(new
        {
            error = "run_in_progress",
            message = "Another sync run for this pair is already in progress; try again shortly.",
        });

    // 409: the pair's module kind has no registered module. Only "calendar" exists today, so in
    // practice this never triggers in production; it is a clear failure for an unknown kind
    // rather than a NullReferenceException once Phase 9 modules (files/clipboard) are added.
    private static IResult NoModule()
        => Results.Conflict(new
        {
            error = "no_module",
            message = "No sync module is registered for this pair's kind.",
        });

    // True when an endpoint references a Graph connected account (explicit, non-empty AccountRef)
    // that the current user does not own. Resolution goes through the adapter (pool-first, legacy
    // fallback): a pool accountId resolves in the pool, a legacy UPN resolves via its derived id.
    // OutlookCom endpoints and endpoints without an explicit AccountRef (which normalize to the
    // user's "default" account) are never treated as missing here.
    private static async Task<bool> ReferencedAccountIsMissingAsync(
        Endpoint endpoint, ILegacyConnectedAccountAdapter adapter, CancellationToken ct)
    {
        if (string.Equals(endpoint.Provider, ProviderRegistry.OutlookCom, StringComparison.Ordinal))
            return false;
        if (string.IsNullOrWhiteSpace(endpoint.AccountRef))
            return false;
        return await ResolveAccountForUserAsync(endpoint.AccountRef, adapter, ct) is null;
    }

    // Shared account-resolution helper for the read/validation surface (list-calendars +
    // create-pair). Resolves an endpoint's accountRef to the current user's CalendarAccount via
    // the adapter (pool-first, legacy fallback). Returns null when the ref resolves to no account
    // the user owns. DRY: both the calendars endpoint and the pair validation route through this so
    // a pool accountId and a legacy UPN are treated identically everywhere.
    private static async Task<CalendarAccount?> ResolveAccountForUserAsync(
        string accountRef, ILegacyConnectedAccountAdapter adapter, CancellationToken ct)
    {
        // Canonicalize first: a legacy UPN ("" / "default" / a real UPN) becomes its derived id,
        // a real pool accountId stays itself. ResolveAsync then looks that id up pool-first, then
        // legacy-wrapped. Both adapter stores are user-scoped, so a foreign ref resolves to null.
        var accountId = await adapter.ResolveAccountIdAsync(accountRef, ct).ConfigureAwait(false);
        return await adapter.ResolveAsync(accountId, ct).ConfigureAwait(false);
    }

    // Display name for a pool account on the listing: prefer the user-facing display name, then the
    // mailbox email, then a generic fallback so the panel never shows an empty label.
    private static string AccountDisplayName(CalendarAccount account)
    {
        if (!string.IsNullOrWhiteSpace(account.DisplayName))
            return account.DisplayName!;
        if (!string.IsNullOrWhiteSpace(account.AccountEmail))
            return account.AccountEmail;
        return "Connected account";
    }

    // §B-4 — true when source and destination address the SAME calendar, which would make a run
    // sweep the calendar it just read. For Graph endpoints the account is compared on the
    // canonical accountId (legacy UPN and pool accountId for the same account collapse to one id);
    // for two OutlookCom (COM) endpoints there is no server account, so we compare on the device
    // reference (AccountRef) instead. The calendar id is then compared within the same account.
    //
    // §A-2 — cross-representation self-mirror is NOW covered (by MAILBOX). The same Microsoft
    // mailbox can be connected BOTH as a legacy UPN account AND as a pool account; the pool account
    // uses a fresh Guid, so the two refs do NOT collapse on accountId. To stop a destructive
    // self-mirror in that case, when the accountId comparison does NOT already flag sameness we
    // ALSO resolve each Graph endpoint to its canonical mailbox email (via the adapter) and compare
    // those case-insensitively, in addition to the calendar id. The accountId comparison is kept as
    // the primary, fast collapse; the email comparison is the extra net for the cross-rep case.
    //
    // TODO (Track B): once the pair model carries a pinnedDeviceId for COM endpoints, compare on
    // deviceId + calendar here. The COM-pin/lease itself is Track B; this method only enforces
    // origin != destination today.
    internal static Task<bool> IsSameSourceAndDestinationAsync(
        Endpoint source, Endpoint destination, ILegacyConnectedAccountAdapter adapter, CancellationToken ct)
        => EndpointsAddressSameCalendarAsync(source, destination, adapter, ct);

    // Canonical endpoint-sameness: true when two endpoints address the SAME calendar after
    // canonicalizing the account reference. This is the shared core behind BOTH the §B-4 self-mirror
    // check (source vs destination) AND the §F2 destination_is_current cleanup guard (the destination
    // to clean vs the pair's CURRENT destination). It MUST be used wherever a raw AccountRef Ordinal
    // comparison would be wrong, because a single Microsoft mailbox can be referenced as a legacy UPN
    // OR as a fresh pool accountId — those strings differ but address the same calendar.
    //
    // For Graph endpoints the account is collapsed onto the canonical accountId; when those still
    // differ (legacy-UPN account vs pool account for one mailbox), the canonical mailbox email is the
    // extra cross-representation net. For two OutlookCom (COM) endpoints there is no server account,
    // so the device reference (AccountRef) identifies the device-side calendar.
    internal static async Task<bool> EndpointsAddressSameCalendarAsync(
        Endpoint source, Endpoint destination, ILegacyConnectedAccountAdapter adapter, CancellationToken ct)
    {
        // Different providers can never address the same calendar (a device-side OutlookCom
        // source is a different surface from a server-side Graph account), so they are always OK.
        if (!string.Equals(source.Provider, destination.Provider, StringComparison.Ordinal))
            return false;

        // For two OutlookCom (COM) endpoints there is no server account, so the device reference
        // (AccountRef) identifies the device-side calendar; compare it plus the calendar id.
        if (string.Equals(source.Provider, ProviderRegistry.OutlookCom, StringComparison.Ordinal))
        {
            return string.Equals(source.AccountRef ?? "", destination.AccountRef ?? "", StringComparison.Ordinal)
                && string.Equals(source.CalendarId, destination.CalendarId, StringComparison.Ordinal);
        }

        // Graph: the calendar id must differ within the same account. Sameness on the canonical
        // accountId catches two refs of the SAME representation (legacy UPN <-> its derived id, or
        // a pool id to itself).
        var sourceId = await adapter.ResolveAccountIdAsync(source.AccountRef, ct).ConfigureAwait(false);
        var destId = await adapter.ResolveAccountIdAsync(destination.AccountRef, ct).ConfigureAwait(false);
        var sameCalendar = string.Equals(source.CalendarId, destination.CalendarId, StringComparison.Ordinal);

        if (string.Equals(sourceId, destId, StringComparison.Ordinal))
            return sameCalendar;

        // §A-2 — distinct accountIds but the SAME mailbox (legacy UPN account vs pool account for
        // one mailbox). Resolve each id to its canonical mailbox email and compare; combined with
        // the same calendar id this is the cross-representation self-mirror that accountId misses.
        if (!sameCalendar)
            return false;

        var sourceEmail = NormalizeMailbox((await adapter.ResolveAsync(sourceId, ct).ConfigureAwait(false))?.AccountEmail);
        var destEmail = NormalizeMailbox((await adapter.ResolveAsync(destId, ct).ConfigureAwait(false))?.AccountEmail);

        // A blank/unknown mailbox is NOT a match — only two resolved, equal mailboxes self-mirror.
        return sourceEmail.Length > 0 && string.Equals(sourceEmail, destEmail, StringComparison.Ordinal);
    }

    // Canonical mailbox key: trimmed + lower-invariant, empty when null/blank. Used so two
    // representations of one mailbox compare equal regardless of casing/whitespace.
    private static string NormalizeMailbox(string? email) =>
        string.IsNullOrWhiteSpace(email) ? "" : email.Trim().ToLowerInvariant();

    // The comparable mailbox of a legacy account, normalized. A legacy account is keyed by its UPN,
    // which IS its mailbox — EXCEPT the literal "default" sentinel, which is the single-account case
    // with no real email and therefore returns empty so it is never fused with a pool account.
    private static string LegacyMailbox(string? userPrincipalName) =>
        string.Equals(userPrincipalName, "default", StringComparison.Ordinal)
            ? ""
            : NormalizeMailbox(userPrincipalName);

    private static (DateTimeOffset from, DateTimeOffset to) Window(ServerOptions opts)
    {
        var today = DateTimeOffset.UtcNow.Date;
        var from = new DateTimeOffset(today, TimeSpan.Zero);
        return (from, from.AddDays(opts.SyncWindowDays));
    }

    // §A-3 read-failure decision (public + static so it is unit-testable without standing up
    // the whole HTTP pipeline). True => the source read failed transiently and the caller must
    // NOT run the mirror: report Partial and retry later. False => not transient (or a real
    // cancellation), so the exception propagates. A cancellation requested by the caller is
    // never "transient" — it must surface as a cancellation, not a retryable partial.
    public static bool IsTransientReadFailure(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (ex is OperationCanceledException)
            return false;
        return SyncErrorClassifier.Classify(ex) == SyncErrorKind.Transient;
    }

    // A transient source read failure aborts before the mirror, so nothing was created,
    // updated or deleted this run. We surface the SAME Partial=true contract as a partial
    // upsert (Created/Updated/Deleted all zero, Partial=true, the read error in Failures) so
    // the client's "partial -> retry later" handling covers read failures too, and the run is
    // recorded as a partial rather than a 500.
    public static MirrorResult PartialReadResult(Exception ex)
        => new MirrorResult
        {
            Partial  = true,
            Failures = new List<string> { $"Source read failed (transient): {ex.Message}" },
        };

    // Track B — resolve, with a SINGLE device-list read, the name + online flag of every device a
    // COM-pinned pair is pinned to. Returns an empty map when no pair carries a pin, so the common
    // all-Graph case does not even touch the device store. Both stores are user-scoped, so a pin that
    // points at a device the caller cannot see simply has no entry (treated as unknown / offline).
    private static async Task<IReadOnlyDictionary<string, (string Name, bool Online)>> BuildDeviceMapAsync(
        IReadOnlyCollection<SyncPair> pairs, DeviceService devices, CancellationToken ct)
    {
        var pinnedIds = pairs
            .Where(p => !string.IsNullOrWhiteSpace(p.PinnedDeviceId))
            .Select(p => p.PinnedDeviceId!)
            .ToHashSet(StringComparer.Ordinal);
        if (pinnedIds.Count == 0)
            return new Dictionary<string, (string, bool)>(StringComparer.Ordinal);

        var now = DateTimeOffset.UtcNow;
        var all = await devices.ListForCurrentUserAsync(ct).ConfigureAwait(false);
        return all
            .Where(d => pinnedIds.Contains(d.Id))
            .ToDictionary(
                d => d.Id,
                d => (d.Name, d.LeaseUntil is { } until && until > now),
                StringComparer.Ordinal);
    }

    // Track B — projection that carries the pair plus its COM-pin resolution for the UI. For a
    // non-COM-pinned pair (no PinnedDeviceId) the three pinned* fields are null/false but still
    // present so the UI shape is stable. The pair's own fields (Id, Name, Source, ...) are spread by
    // serializing the record alongside the resolved device fields.
    private static object EnrichPair(
        SyncPair pair, IReadOnlyDictionary<string, (string Name, bool Online)> deviceMap)
    {
        string? pinnedName = null;
        var pinnedOnline = false;
        if (!string.IsNullOrWhiteSpace(pair.PinnedDeviceId)
            && deviceMap.TryGetValue(pair.PinnedDeviceId!, out var info))
        {
            pinnedName = info.Name;
            pinnedOnline = info.Online;
        }

        return new
        {
            pair.Id,
            pair.Name,
            pair.Source,
            pair.Destination,
            pair.IntervalMin,
            pair.State,
            pair.LastRunUtc,
            pair.LastResult,
            pair.PendingCleanupDestinations,
            pair.PinnedDeviceId,
            pair.SyncRequestedUtc,
            pinnedDeviceName = pinnedName,
            pinnedDeviceOnline = pinnedOnline,
        };
    }

    // Records a completed run (LastRunUtc + LastResult, clearing any satisfied sync-now signal) AND
    // fans the result out over the WS to the user's OTHER live sessions so an open Calendar/Sync
    // screen on another window/machine refreshes without re-opening the screen. The broadcast is
    // best-effort and is issued AFTER the persist so a peer that reloads on the frame reads the new
    // row; userId scopes the fan-out and originDeviceId (the pushing device, empty for a human caller)
    // is excluded — it already has the result. A null broadcaster/userId skips the push (the persist
    // still happens), which keeps the helper usable from paths without an identity in scope.
    private static async Task RecordRunAsync(
        ISyncPairStore store,
        SyncPair pair,
        MirrorResult result,
        CancellationToken ct,
        SyncBroadcaster? broadcaster = null,
        string? userId = null,
        string? originDeviceId = null)
    {
        var now = DateTimeOffset.UtcNow;
        // Track B — a recorded run at/after a pending sync-now request satisfies that request, so the
        // signal is cleared here (no extra ack endpoint). Idempotent: a later run with no pending
        // signal leaves it null. This is the device-pinned run consuming the signal that /request-sync
        // stamped, so the scheduler does not re-trigger the same request forever.
        //
        // Lost-update note: `pair` is the snapshot read at the START of this run. If /request-sync
        // re-stamps a NEWER SyncRequestedUtc while the run is in flight, this clear (now >= requested)
        // would still erase that newer signal, dropping one requested sync. We accept that: the
        // contract is "at most one run per request window", and the scheduler's _lastHandledRequest
        // already coalesces multiple stamps observed in the same window into a single run. A request
        // that arrives mid-run is functionally indistinguishable from one coalesced into the run that
        // is already executing, so no user-visible sync is lost — the in-flight run covers it.
        var clearSignal = pair.SyncRequestedUtc is { } requested && now >= requested;
        var updated = pair with
        {
            LastRunUtc = now,
            LastResult = result,
            SyncRequestedUtc = clearSignal ? null : pair.SyncRequestedUtc,
        };
        await store.UpdateAsync(updated, ct).ConfigureAwait(false);

        // Live-push the recorded run to the user's OTHER sessions. Best-effort: a dead peer socket
        // never fails the run. Skipped when no broadcaster/identity is in scope.
        if (broadcaster is not null && !string.IsNullOrEmpty(userId))
            await broadcaster
                .BroadcastPairRunAsync(userId!, originDeviceId ?? string.Empty, pair.Id, result, now, ct)
                .ConfigureAwait(false);
    }
}
