using FluentValidation;
using ZyncMaster.Graph;

namespace ZyncMaster.Server;

// Calendar v2 endpoints (spec §8): manual event creation with replicate-on-create, per-event
// replica fan-out, replica link mutations, write-back respond, and the unified day view. All
// behind RequireIdentityBearer; ownership is enforced by the user-scoped stores (a foreign id
// resolves to null -> 404, indistinguishable from nonexistent).
//
// Route note (documented deviation): the spec sketch's {ref} is realized as the two route
// segments {accountId}/{eventId} — Graph event ids are opaque single segments, and resolving
// the account through the user-scoped store IS the ownership check.
public static class CalendarV2Endpoints
{
    public static void MapCalendarV2Endpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Create an event on the user's own calendar (+ optional replicate-on-create, spec §4).
        // Body/location go ONLY to the origin; the fan-out below can only carry ReplicaDrafts.
        app.MapPost("/api/calendar/events", async (
            CreateEventRequest? request,
            ICalendarAccountStore accounts,
            Func<string, IReplicaGraphClient> clients,
            ReplicaService replicas,
            CancellationToken ct) =>
        {
            var body = request ?? new CreateEventRequest();
            var validation = new CreateEventRequestValidator().Validate(body);
            if (!validation.IsValid)
                return Results.ValidationProblem(validation.ToDictionary());

            var account = await accounts.GetAsync(body.AccountId!, ct);
            if (account is null)
                return Results.NotFound(new { error = "account_not_found" });
            if (account.Kind != AccountKind.Graph)
                return Results.UnprocessableEntity(new
                {
                    error = "com_writes_deferred",
                    message = "Creating events on COM accounts requires the v1.1 COM write channel.",
                });
            // Spec §4: create/edit/cancel need readwrite on the account WHERE THE EVENT LIVES.
            if (account.Scope != AccountScope.ReadWrite)
                return Results.Conflict(new
                {
                    error = "readwrite_scope_required",
                    message = "Upgrade the account scope to create events from here.",
                });

            var draft = new OriginEventDraft
            {
                Subject = body.Title!.Trim(),
                BodyHtml = body.Body ?? "",
                Location = body.Location ?? "",
                Start = body.Start!.Value,
                End = body.End!.Value,
                TimeZoneId = string.IsNullOrWhiteSpace(body.TimeZoneId) ? "UTC" : body.TimeZoneId!,
                IsAllDay = body.IsAllDay,
                ShowAs = string.IsNullOrWhiteSpace(body.ShowAs) ? "busy" : body.ShowAs!,
            };
            var eventId = await clients(body.AccountId!).CreateOriginEventAsync(body.CalendarId!, draft, ct);

            object? fanOut = null;
            if (body.Replicas is { Count: > 0 })
            {
                // Stable id decision 9: events we created have no iCalUId yet; seed with the
                // Graph id (propagation addresses by SourceGraphEventId anyway).
                var snapshot = new SourceEventSnapshot
                {
                    GraphEventId = eventId,
                    StableId = ZyncMaster.Core.OccurrenceId.For(eventId, draft.Start),
                    Subject = draft.Subject,
                    Start = draft.Start,
                    End = draft.End,
                    TimeZoneId = draft.TimeZoneId,
                    IsAllDay = draft.IsAllDay,
                    ShowAs = draft.ShowAs,
                };
                var destinations = body.Replicas
                    .Select(d => new ReplicaDestinationRequest(d.AccountId ?? "", d.CalendarId ?? "", d.Title ?? ""))
                    .ToList();
                var outcome = await replicas.FanOutFromSnapshotAsync(
                    body.AccountId!, snapshot, destinations, ruleId: null, ct);
                if (outcome.ErrorCode is not null)
                    return MapServiceError(outcome);
                fanOut = new { created = outcome.Created.Select(ToLinkDto), failures = outcome.Failures };
            }

            return Results.Created($"/api/calendar/events/{body.AccountId}/{eventId}",
                new { eventId, replicas = fanOut });
        }).RequireIdentityBearer();

        // Replica fan-out for an existing event (spec §3/§8).
        app.MapPost("/api/calendar/events/{accountId}/{eventId}/replicas", async (
            string accountId,
            string eventId,
            FanOutRequest? request,
            ReplicaService replicas,
            CancellationToken ct) =>
        {
            var body = request ?? new FanOutRequest(null);
            var validation = new FanOutRequestValidator().Validate(body);
            if (!validation.IsValid)
                return Results.ValidationProblem(validation.ToDictionary());

            var destinations = body.Destinations!
                .Select(d => new ReplicaDestinationRequest(d.AccountId ?? "", d.CalendarId ?? "", d.Title ?? ""))
                .ToList();
            var outcome = await replicas.FanOutAsync(accountId, eventId, destinations, ruleId: null, ct);
            if (outcome.ErrorCode is not null)
                return MapServiceError(outcome);

            return Results.Ok(new { created = outcome.Created.Select(ToLinkDto), failures = outcome.Failures });
        }).RequireIdentityBearer();

        // Edit the manual mask title, or recreate a broken replica (exit 1 of the broken flow).
        app.MapPatch("/api/calendar/replicas/{linkId}", async (
            string linkId,
            UpdateReplicaRequest? request,
            ReplicaService replicas,
            CancellationToken ct) =>
        {
            var body = request ?? new UpdateReplicaRequest(null, false);
            var validation = new UpdateReplicaRequestValidator().Validate(body);
            if (!validation.IsValid)
                return Results.ValidationProblem(validation.ToDictionary());

            var outcome = body.Recreate
                ? await replicas.RecreateAsync(linkId, ct)
                : await replicas.UpdateTitleAsync(linkId, body.Title!, ct);
            if (outcome.ErrorCode is not null)
                return MapServiceError(outcome);

            return Results.Ok(new { status = "ok", created = outcome.Created.Select(ToLinkDto) });
        }).RequireIdentityBearer();

        // Remove a replica (delete in Graph + tombstone) — also the "discard" exit for broken.
        app.MapDelete("/api/calendar/replicas/{linkId}", async (
            string linkId,
            ReplicaService replicas,
            CancellationToken ct) =>
        {
            var outcome = await replicas.RemoveAsync(linkId, ct);
            if (outcome.ErrorCode is not null)
                return MapServiceError(outcome);
            return Results.NoContent();
        }).RequireIdentityBearer();

        // Task 12: POST /api/calendar/events/{accountId}/{eventId}/respond
        // Task 16: GET  /api/calendar/day
    }

    internal static object ToLinkDto(ReplicaLink l) => new
    {
        id = l.Id,
        sourceAccountId = l.SourceAccountId,
        sourceEventId = l.SourceEventId,
        destinationAccountId = l.DestinationAccountId,
        destinationCalendarId = l.DestinationCalendarId,
        destinationEventId = l.DestinationEventId,
        maskTitle = l.MaskTitle,
        ruleId = l.RuleId,
        status = l.Status.ToString().ToLowerInvariant(),
    };

    // One mapping for every ReplicaService rejection: *_not_found -> 404; scope/state conflicts
    // -> 409; semantic rejections (anti-loop, COM deferral, mask rules) -> 422.
    internal static IResult MapServiceError(FanOutOutcome outcome)
    {
        var payload = new { error = outcome.ErrorCode, message = outcome.ErrorMessage };
        return outcome.ErrorCode switch
        {
            "source_account_not_found" or "source_event_not_found"
                or "destination_account_not_found" or "link_not_found"
                => Results.NotFound(payload),
            "readwrite_scope_required" or "link_not_active" or "link_not_broken"
                or "source_event_cancelled"
                => Results.Conflict(payload),
            _ => Results.UnprocessableEntity(payload),
        };
    }
}

// Request DTOs + FluentValidation (repo rule: explicit validators, never DataAnnotations).

public sealed record ReplicaDestinationDto(string? AccountId, string? CalendarId, string? Title);

public sealed record FanOutRequest(List<ReplicaDestinationDto>? Destinations);

public sealed class FanOutRequestValidator : AbstractValidator<FanOutRequest>
{
    public FanOutRequestValidator()
    {
        RuleFor(x => x.Destinations)
            .NotNull().WithMessage("destinations is required.")
            .Must(d => d!.Count > 0).When(x => x.Destinations is not null)
            .WithMessage("destinations must not be empty.");
        RuleForEach(x => x.Destinations).ChildRules(d =>
        {
            d.RuleFor(x => x.AccountId).NotEmpty();
            d.RuleFor(x => x.CalendarId).NotEmpty();
            d.RuleFor(x => x.Title).NotEmpty().MaximumLength(256)
                .WithMessage("Every destination needs a manual title (it never defaults to the source subject).");
        });
    }
}

public sealed record CreateEventRequest
{
    public string? AccountId { get; init; }
    public string? CalendarId { get; init; }
    public string? Title { get; init; }
    public DateTimeOffset? Start { get; init; }
    public DateTimeOffset? End { get; init; }
    public string? TimeZoneId { get; init; }
    public bool IsAllDay { get; init; }
    public string? ShowAs { get; init; }
    public string? Body { get; init; }
    public string? Location { get; init; }
    public List<ReplicaDestinationDto>? Replicas { get; init; }
}

public sealed class CreateEventRequestValidator : AbstractValidator<CreateEventRequest>
{
    private static readonly string[] ValidShowAs =
        { "free", "tentative", "busy", "oof", "workingElsewhere" };

    public CreateEventRequestValidator()
    {
        RuleFor(x => x.AccountId).NotEmpty();
        RuleFor(x => x.CalendarId).NotEmpty();
        RuleFor(x => x.Title).NotEmpty().MaximumLength(256);
        RuleFor(x => x.Start).NotNull();
        RuleFor(x => x.End).NotNull();
        RuleFor(x => x)
            .Must(x => x.Start is null || x.End is null || x.End > x.Start)
            .WithMessage("end must be after start.");
        RuleFor(x => x.ShowAs)
            .Must(s => string.IsNullOrWhiteSpace(s) ||
                ValidShowAs.Contains(s, StringComparer.OrdinalIgnoreCase))
            .WithMessage("Invalid showAs.");
        RuleForEach(x => x.Replicas).ChildRules(d =>
        {
            d.RuleFor(x => x.AccountId).NotEmpty();
            d.RuleFor(x => x.CalendarId).NotEmpty();
            d.RuleFor(x => x.Title).NotEmpty().MaximumLength(256);
        });
    }
}

public sealed record UpdateReplicaRequest(string? Title, bool Recreate);

public sealed class UpdateReplicaRequestValidator : AbstractValidator<UpdateReplicaRequest>
{
    public UpdateReplicaRequestValidator()
    {
        // Exactly one operation per call: a title edit OR a recreate.
        RuleFor(x => x)
            .Must(x => x.Recreate ? string.IsNullOrEmpty(x.Title) : !string.IsNullOrWhiteSpace(x.Title))
            .WithMessage("Provide either a non-empty title, or recreate=true (not both).");
        RuleFor(x => x.Title).MaximumLength(256);
    }
}
