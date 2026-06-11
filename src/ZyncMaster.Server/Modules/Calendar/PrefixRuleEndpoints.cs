using FluentValidation;

namespace ZyncMaster.Server;

// CRUD for prefix rules (spec §5/§8). Destination membership IS the per-calendar two-way flag,
// so the only list-shaped field is `destinations`. Destination accounts are validated at WRITE
// time (exist + Graph + readwrite): a rule must fail at creation, not silently in the cron.
public static class PrefixRuleEndpoints
{
    public static void MapPrefixRuleEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/calendar/prefix-rules", async (
            IPrefixRuleStore rules, CancellationToken ct) =>
        {
            var list = await rules.ListAsync(ct);
            return Results.Ok(list.Select(ToDto));
        }).RequireIdentityBearer();

        app.MapPost("/api/calendar/prefix-rules", async (
            PrefixRuleRequest? request,
            IPrefixRuleStore rules,
            ICalendarAccountStore accounts,
            CancellationToken ct) =>
        {
            var body = request ?? new PrefixRuleRequest();
            var validation = new PrefixRuleRequestValidator().Validate(body);
            if (!validation.IsValid)
                return Results.ValidationProblem(validation.ToDictionary());

            var destinationError = await ValidateDestinationsAsync(body, accounts, ct);
            if (destinationError is not null)
                return destinationError;

            var rule = await rules.AddAsync(ToDomain(Guid.NewGuid().ToString("N"), body), ct);
            return Results.Created($"/api/calendar/prefix-rules/{rule.Id}", ToDto(rule));
        }).RequireIdentityBearer();

        app.MapPut("/api/calendar/prefix-rules/{id}", async (
            string id,
            PrefixRuleRequest? request,
            IPrefixRuleStore rules,
            ICalendarAccountStore accounts,
            CancellationToken ct) =>
        {
            var body = request ?? new PrefixRuleRequest();
            var validation = new PrefixRuleRequestValidator().Validate(body);
            if (!validation.IsValid)
                return Results.ValidationProblem(validation.ToDictionary());

            var destinationError = await ValidateDestinationsAsync(body, accounts, ct);
            if (destinationError is not null)
                return destinationError;

            var updated = await rules.UpdateAsync(ToDomain(id, body), ct);
            return updated
                ? Results.Ok(new { status = "ok" })
                : Results.NotFound(new { error = "rule_not_found" });
        }).RequireIdentityBearer();

        app.MapDelete("/api/calendar/prefix-rules/{id}", async (
            string id, IPrefixRuleStore rules, CancellationToken ct) =>
        {
            var removed = await rules.RemoveAsync(id, ct);
            return removed ? Results.NoContent() : Results.NotFound(new { error = "rule_not_found" });
        }).RequireIdentityBearer();
    }

    // Every destination account must exist (user-scoped -> foreign == nonexistent == 404), be
    // Graph and readwrite. Mirrors ReplicaService's fan-out checks so the rule cannot encode a
    // destination the engine would reject on every run.
    private static async Task<IResult?> ValidateDestinationsAsync(
        PrefixRuleRequest body, ICalendarAccountStore accounts, CancellationToken ct)
    {
        foreach (var d in body.Destinations!)
        {
            var account = await accounts.GetAsync(d.AccountId!, ct);
            if (account is null)
                return Results.NotFound(new
                {
                    error = "destination_account_not_found",
                    message = $"Unknown account '{d.AccountId}'.",
                });
            if (account.Kind != AccountKind.Graph)
                return Results.UnprocessableEntity(new
                {
                    error = "destination_not_graph",
                    message = "Replicas can only be written to Graph accounts.",
                });
            if (account.Scope != AccountScope.ReadWrite)
                return Results.Conflict(new
                {
                    error = "readwrite_scope_required",
                    message = $"Account '{d.AccountId}' is read-only; upgrade its scope first.",
                });
        }
        return null;
    }

    private static PrefixRule ToDomain(string id, PrefixRuleRequest body) => new()
    {
        Id = id,
        Prefix = body.Prefix!.Trim(),
        MaskTitle = body.MaskTitle!.Trim(),
        Enabled = body.Enabled,
        SortOrder = body.SortOrder,
        Destinations = body.Destinations!
            .Select(d => new PrefixRuleDestination(d.AccountId!, d.CalendarId!))
            .ToList(),
    };

    private static object ToDto(PrefixRule r) => new
    {
        id = r.Id,
        prefix = r.Prefix,
        maskTitle = r.MaskTitle,
        enabled = r.Enabled,
        sortOrder = r.SortOrder,
        destinations = r.Destinations.Select(d => new
        {
            accountId = d.AccountId,
            calendarId = d.CalendarId,
        }),
    };
}

public sealed record PrefixRuleDestinationDto(string? AccountId, string? CalendarId);

public sealed record PrefixRuleRequest
{
    public string? Prefix { get; init; }
    public string? MaskTitle { get; init; }
    public bool Enabled { get; init; } = true;
    public int SortOrder { get; init; }
    public List<PrefixRuleDestinationDto>? Destinations { get; init; }
}

public sealed class PrefixRuleRequestValidator : AbstractValidator<PrefixRuleRequest>
{
    public PrefixRuleRequestValidator()
    {
        // The brackets are SYNTAX ("[Lunch] X"), never part of the stored prefix.
        RuleFor(x => x.Prefix)
            .NotEmpty().MaximumLength(64)
            .Must(p => p is null || (!p.Contains('[') && !p.Contains(']')))
            .WithMessage("prefix must not contain brackets — they are added by the [prefix] syntax.");
        RuleFor(x => x.MaskTitle).NotEmpty().MaximumLength(256);
        RuleFor(x => x.SortOrder).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Destinations)
            .NotNull().WithMessage("destinations is required.")
            .Must(d => d!.Count > 0).When(x => x.Destinations is not null)
            .WithMessage("destinations must not be empty — membership IS the two-way flag.");
        RuleForEach(x => x.Destinations).ChildRules(d =>
        {
            d.RuleFor(x => x.AccountId).NotEmpty();
            d.RuleFor(x => x.CalendarId).NotEmpty();
        });
    }
}
