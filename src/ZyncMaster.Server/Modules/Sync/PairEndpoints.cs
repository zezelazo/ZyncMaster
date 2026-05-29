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
        }).RequireAuthorization();

        app.MapGet("/api/accounts/{accountRef}/calendars", async (
            string accountRef, ProviderRegistry registry) =>
        {
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
        }).RequireAuthorization();

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
        }).RequireAuthorization();

        app.MapGet("/api/pairs", async (ISyncPairStore store) =>
            Results.Ok(await store.ListAsync())).RequireAuthorization();

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
        }).RequireAuthorization();

        app.MapDelete("/api/pairs/{id}", async (string id, ISyncPairStore store) =>
        {
            await store.RemoveAsync(id);
            return Results.NoContent();
        }).RequireAuthorization();
    }
}
