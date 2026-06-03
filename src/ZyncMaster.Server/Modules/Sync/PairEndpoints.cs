using ZyncMaster.Core;
using ZyncMaster.Graph;
using ZyncMaster.Server.Modules.Calendar;

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
        // id is UuidV5(namespace, "{userId}|{upn}"). The two never collide, so a pool account and a
        // legacy account are NEVER collapsed here — even for the same underlying mailbox they surface
        // as two entries (distinct accountIds). The seen-set below only guards against listing the
        // SAME legacy ref twice (e.g. a UPN whose canonical id already came from another legacy row);
        // it does not — and cannot, on accountId — merge a pool/legacy pair for one mailbox.
        app.MapGet("/api/accounts", async (
            ICalendarAccountStore pool,
            IConnectedAccountStore legacy,
            ILegacyConnectedAccountAdapter adapter,
            CancellationToken ct) =>
        {
            var pooled = await pool.ListAsync(ct);
            var legacyAccounts = await legacy.ListAsync(ct);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var infos = new List<AccountInfo>();

            foreach (var account in pooled)
            {
                seen.Add(account.Id);
                infos.Add(new AccountInfo
                {
                    AccountRef = account.Id,
                    DisplayName = AccountDisplayName(account),
                    IsDefault = false,
                });
            }

            foreach (var account in legacyAccounts)
            {
                // Skip a legacy account whose canonical id already came from the pool.
                var canonicalId = await adapter.ResolveAccountIdAsync(account.UserPrincipalName, ct);
                if (!seen.Add(canonicalId))
                    continue;

                infos.Add(new AccountInfo
                {
                    // Keep the legacy ref as-is (UPN / "default") so existing pairs and tests that
                    // reference the legacy account by UPN keep resolving unchanged.
                    AccountRef = account.UserPrincipalName,
                    DisplayName = string.Equals(account.UserPrincipalName, "default", StringComparison.Ordinal)
                        ? "Connected account"
                        : account.UserPrincipalName,
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
        }).RequireCookie();

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

            // Disable every pair whose source or destination resolves to this canonical accountId
            // so a removed account never leaves an active pair pointing at a forgotten account.
            // Reuses the same canonical-id disable routine the pool-delete endpoint uses.
            var affectedPairIds = await CalendarConnectEndpoints
                .DisablePairsForAccountAsync(accountId, pairs, adapter, ct);

            // Remove from whichever store actually backs the account: a real pool account by its
            // canonical id, otherwise the legacy store by the raw UPN ref (as before).
            if (await pool.GetAsync(accountId, ct) is not null)
                await pool.RemoveAsync(accountId, ct);
            else
                await legacy.RemoveAsync(accountRef, ct);

            return Results.Ok(new { affectedPairIds });
        }).RequireCookie();

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
        }).RequireCookie();

        app.MapPost("/api/pairs", async (
            CreatePairRequest req,
            ISyncPairStore store,
            ILegacyConnectedAccountAdapter adapter,
            CancellationToken ct) =>
        {
            var validation = new CreatePairRequestValidator().Validate(req);
            if (!validation.IsValid)
                return Results.ValidationProblem(validation.ToDictionary());

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
                IntervalMin = req.IntervalMin,
                State = "active",
            };
            await store.AddAsync(pair);
            return Results.Ok(pair);
        }).RequireCookie();

        app.MapGet("/api/pairs", async (ISyncPairStore store) =>
            Results.Ok(await store.ListAsync())).RequireCookie();

        app.MapGet("/api/pairs/{id}", async (string id, ISyncPairStore store, CancellationToken ct) =>
        {
            // The store filters by current user, so a pair owned by another user (or absent)
            // resolves to null -> 404. Never 403: don't reveal that the id exists elsewhere.
            var pair = await store.GetAsync(id, ct);
            return pair is null ? Results.NotFound() : Results.Ok(pair);
        }).RequireCookie();

        app.MapPatch("/api/pairs/{id}", async (string id, UpdatePairRequest req, ISyncPairStore store) =>
        {
            var validation = new UpdatePairRequestValidator().Validate(req);
            if (!validation.IsValid)
                return Results.ValidationProblem(validation.ToDictionary());

            var existing = await store.GetAsync(id);
            if (existing is null)
                return Results.NotFound();

            var updated = existing with
            {
                Name = req.Name ?? existing.Name,
                IntervalMin = req.IntervalMin ?? existing.IntervalMin,
                State = req.State ?? existing.State,
            };
            await store.UpdateAsync(updated);
            return Results.Ok(updated);
        }).RequireCookie();

        app.MapDelete("/api/pairs/{id}", async (string id, ISyncPairStore store, CancellationToken ct) =>
        {
            // Confirm ownership before deleting: a cross-user (or absent) id resolves to null
            // in the user-scoped store -> 404, so RemoveAsync never silently no-ops on a pair
            // the caller doesn't own and we don't leak its existence.
            if (await store.GetAsync(id, ct) is null)
                return Results.NotFound();

            await store.RemoveAsync(id, ct);
            return Results.NoContent();
        }).RequireCookie();

        app.MapPost("/api/pairs/{id}/push", async (
            string id,
            PushRequest req,
            ISyncPairStore store,
            ISyncRunLock runLock,
            ProviderRegistry registry,
            Microsoft.Extensions.Options.IOptions<ServerOptions> opts,
            CancellationToken ct) =>
        {
            ArgumentNullException.ThrowIfNull(req);

            var pair = await store.GetAsync(id, ct);
            if (pair is null)
                return Results.NotFound();

            // §B-1 — acquire the per-pair run lock INSIDE the endpoint before the destructive
            // mirror. If another executor (manual run, overlapping tick) holds it, skip with
            // 409 instead of running a second concurrent sweep against the same calendar.
            await using var handle = await runLock
                .TryAcquireAsync(id, LockTtl(opts.Value), owner: "push", ct)
                .ConfigureAwait(false);
            if (handle is null)
                return RunLockBusy();

            var writer = registry.ResolveWriter(pair.Destination);
            var (from, to) = Window(opts.Value);
            var result = await writer
                .MirrorAsync(pair.Destination.CalendarId, req.Events, ReminderMinutes, from, to, ct)
                .ConfigureAwait(false);

            await RecordRunAsync(store, pair, result, ct).ConfigureAwait(false);
            return Results.Ok(result);
        }).RequireApiKey();

        app.MapPost("/api/pairs/{id}/run", async (
            string id,
            ISyncPairStore store,
            ISyncRunLock runLock,
            SyncModuleRegistry modules,
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

            var (from, to) = Window(opts.Value);

            // Delegate the read + destructive mirror to the calendar module. The §A-3 transient
            // read guard and the conditional window sweep live inside the module / CalendarMirror.
            var outcome = await module.ExecuteAsync(pair, from, to, ct).ConfigureAwait(false);
            if (outcome.NoServerReader)
            {
                // OutlookCom sources have no server-side read; their events arrive via /push.
                return Results.Conflict(new
                {
                    error = "no_server_reader",
                    message = "This source provider has no server reader; use the push endpoint.",
                });
            }

            await RecordRunAsync(store, pair, outcome.Result!, ct).ConfigureAwait(false);
            return Results.Ok(outcome.Result);
        }).RequireCookieOrApiKey();
    }

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
    internal static async Task<bool> IsSameSourceAndDestinationAsync(
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

    private static Task RecordRunAsync(ISyncPairStore store, SyncPair pair, MirrorResult result, CancellationToken ct)
    {
        var updated = pair with
        {
            LastRunUtc = DateTimeOffset.UtcNow,
            LastResult = result,
        };
        return store.UpdateAsync(updated, ct);
    }
}
