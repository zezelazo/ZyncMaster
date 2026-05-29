namespace ZyncMaster.Server;

public static class PairEndpoints
{
    public static void MapPairEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/accounts", async (IConnectedAccountStore accounts) =>
        {
            var list = await accounts.ListAsync();
            var infos = list.Select(a => new AccountInfo
            {
                AccountRef = a.UserPrincipalName,
                DisplayName = string.Equals(a.UserPrincipalName, "default", StringComparison.Ordinal)
                    ? "Connected account"
                    : a.UserPrincipalName,
                IsDefault = string.Equals(a.UserPrincipalName, "default", StringComparison.Ordinal)
                    || list.Count == 1,
            }).ToList();
            return Results.Ok(infos);
        }).RequireCookie();

        app.MapDelete("/api/accounts/{accountRef}", async (
            string accountRef,
            IConnectedAccountStore accounts,
            ISyncPairStore pairs,
            CancellationToken ct) =>
        {
            // The account store is user-scoped, so an account that does not belong to the
            // caller resolves to null. Return 404 (not 403) so we never leak the existence
            // of another user's account, and never run the unlink cascade against it.
            if (await accounts.GetAsync(accountRef, ct) is null)
                return Results.NotFound();

            // Disable any pair referencing this account on either side so a stale pair never
            // tries to sync against a forgotten account. Dedupe ids appearing on both sides.
            var byDest = await pairs.ListByDestinationAccountAsync(accountRef, ct);
            var bySrc = await pairs.ListBySourceAccountAsync(accountRef, ct);

            var affected = byDest.Concat(bySrc)
                .GroupBy(p => p.Id, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();

            foreach (var pair in affected)
            {
                if (string.Equals(pair.State, "disabled", StringComparison.Ordinal))
                    continue;
                await pairs.UpdateAsync(pair with { State = "disabled" }, ct);
            }

            await accounts.RemoveAsync(accountRef, ct);

            return Results.Ok(new { affectedPairIds = affected.Select(p => p.Id).ToList() });
        }).RequireCookie();

        app.MapGet("/api/accounts/{accountRef}/calendars", async (
            string accountRef,
            ProviderRegistry registry,
            IConnectedAccountStore accounts,
            CancellationToken ct) =>
        {
            // Only enumerate calendars for an account the caller owns. A cross-user ref
            // resolves to null in the user-scoped store -> 404 (don't leak existence, and
            // don't hit Graph with another user's missing token, which would 500).
            if (await accounts.GetAsync(accountRef, ct) is null)
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

        app.MapPost("/api/pairs", async (CreatePairRequest req, ISyncPairStore store) =>
        {
            var validation = new CreatePairRequestValidator().Validate(req);
            if (!validation.IsValid)
                return Results.ValidationProblem(validation.ToDictionary());

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
            ProviderRegistry registry,
            Microsoft.Extensions.Options.IOptions<ServerOptions> opts,
            CancellationToken ct) =>
        {
            ArgumentNullException.ThrowIfNull(req);

            var pair = await store.GetAsync(id, ct);
            if (pair is null)
                return Results.NotFound();

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
            ProviderRegistry registry,
            Microsoft.Extensions.Options.IOptions<ServerOptions> opts,
            CancellationToken ct) =>
        {
            var pair = await store.GetAsync(id, ct);
            if (pair is null)
                return Results.NotFound();

            var reader = registry.ResolveReader(pair.Source);
            if (reader is null)
            {
                // OutlookCom sources have no server-side read; their events arrive via /push.
                return Results.Conflict(new
                {
                    error = "no_server_reader",
                    message = "This source provider has no server reader; use the push endpoint.",
                });
            }

            var (from, to) = Window(opts.Value);
            var events = await reader.ReadWindowAsync(pair.Source.CalendarId, from, to, ct).ConfigureAwait(false);

            var writer = registry.ResolveWriter(pair.Destination);
            var result = await writer
                .MirrorAsync(pair.Destination.CalendarId, events, ReminderMinutes, from, to, ct)
                .ConfigureAwait(false);

            await RecordRunAsync(store, pair, result, ct).ConfigureAwait(false);
            return Results.Ok(result);
        }).RequireApiKey();
    }

    private const int ReminderMinutes = 30;

    private static (DateTimeOffset from, DateTimeOffset to) Window(ServerOptions opts)
    {
        var today = DateTimeOffset.UtcNow.Date;
        var from = new DateTimeOffset(today, TimeSpan.Zero);
        return (from, from.AddDays(opts.SyncWindowDays));
    }

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
