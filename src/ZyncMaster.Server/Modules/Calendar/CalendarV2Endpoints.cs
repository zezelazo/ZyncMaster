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

        // Write-back toward the ORIGIN (spec §6): cancel (organizer only), accept, decline
        // ("will not attend") or tentative, always with an OPTIONAL user-authored message —
        // the single piece of information that ever crosses to the origin side (§12).
        // COM origins get the explicit v1.1 deferral (queue + CalExport --respond), never
        // a silent failure. linkId (optional) closes a broken link once the write-back lands.
        app.MapPost("/api/calendar/events/{accountId}/{eventId}/respond", async (
            string accountId,
            string eventId,
            RespondRequest? request,
            ICalendarAccountStore accounts,
            IReplicaLinkStore links,
            Func<string, IReplicaGraphClient> clients,
            Func<string, IEventResponder> responders,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            var body = request ?? new RespondRequest(null, null, null);
            var validation = new RespondRequestValidator().Validate(body);
            if (!validation.IsValid)
                return Results.ValidationProblem(validation.ToDictionary());

            var account = await accounts.GetAsync(accountId, ct);
            if (account is null)
                return Results.NotFound(new { error = "account_not_found" });

            if (account.Kind != AccountKind.Graph)
            {
                // Spec §13 deferral, verbatim scope: the COM write-back is v1.1 — a server-side
                // action queue with TTL drained by the pinned device's App, executed through a
                // new CalExport --respond verb (AppointmentItem.Respond + Send over COM). Until
                // then the UI offers only recreate/discard for COM-source broken links.
                return Results.UnprocessableEntity(new
                {
                    error = "com_writeback_deferred",
                    message = "Write-back to COM origins ships in v1.1 via the CalExport respond queue; v1 supports Graph origins only.",
                });
            }

            if (account.Scope != AccountScope.ReadWrite)
                return Results.Conflict(new
                {
                    error = "readwrite_scope_required",
                    message = "Upgrade the account scope to respond from here.",
                });

            var snapshot = await clients(accountId).GetEventAsync(eventId, ct);
            if (snapshot is null)
                return Results.NotFound(new { error = "event_not_found" });

            var responder = responders(accountId);
            switch (body.Action!.ToLowerInvariant())
            {
                case "cancel":
                    if (!snapshot.IsOrganizer)
                        return Results.Conflict(new
                        {
                            error = "organizer_required",
                            message = "Only the organizer can cancel; decline instead.",
                        });
                    if (snapshot.HasAttendees)
                        await responder.CancelMeetingAsync(eventId, body.Message, ct);
                    else
                        // A personal appointment has nobody to notify: cancel == clean,
                        // silent delete (the CalImport rationale, spec §3).
                        await clients(accountId).DeleteEventAsync(eventId, ct);
                    break;
                case "accept":
                    await responder.RespondAsync(eventId, RespondAction.Accept, body.Message, ct);
                    break;
                case "decline":
                    await responder.RespondAsync(eventId, RespondAction.Decline, body.Message, ct);
                    break;
                case "tentative":
                    await responder.RespondAsync(eventId, RespondAction.Tentative, body.Message, ct);
                    break;
            }

            // Broken-link closure (spec §3/§7): confirming the write-back is one of the only
            // two flows that move broken -> tombstone (the other is discarding the link).
            if (!string.IsNullOrEmpty(body.LinkId))
            {
                var link = await links.GetAsync(body.LinkId, ct);
                if (link is not null && link.Status == ReplicaLinkStatus.Broken)
                    await links.UpdateAsync(link with
                    {
                        Status = ReplicaLinkStatus.Tombstone,
                        UpdatedUtc = clock.GetUtcNow(),
                    }, ct);
            }

            return Results.Ok(new { status = "ok" });
        }).RequireIdentityBearer();

        // Unified day view (spec §8/§9): every Graph account live, COM accounts as an explicit
        // "snapshot_unavailable" entry (the App-push snapshot store is future work — visible
        // degradation, never an omitted account). Day window is UTC (plan decision 6); the UI
        // owns the timezone presentation.
        app.MapGet("/api/calendar/day", async (
            string? date,
            string? accounts,
            ICalendarAccountStore accountStore,
            IReplicaLinkStore links,
            Func<string, IReplicaGraphClient> clients,
            CancellationToken ct) =>
        {
            if (!DateOnly.TryParseExact(date ?? "", "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var day))
            {
                return Results.BadRequest(new
                {
                    error = "invalid_date",
                    message = "Expected date=yyyy-MM-dd.",
                });
            }
            var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var to = from.AddDays(1);

            var requested = (accounts ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal);
            var selected = (await accountStore.ListAsync(ct))
                .Where(a => a.Status == "active" && (requested.Count == 0 || requested.Contains(a.Id)))
                .ToList();

            // Replica/mask annotations: every non-tombstone link keyed by its source event.
            var linksBySource = (await links.ListAsync(ct))
                .Where(l => l.Status != ReplicaLinkStatus.Tombstone)
                .ToLookup(l => l.SourceEventId, StringComparer.Ordinal);

            var accountViews = new List<DayAccountDto>();
            foreach (var account in selected)
            {
                if (account.Kind != AccountKind.Graph)
                {
                    accountViews.Add(new DayAccountDto(
                        account.Id, account.AccountEmail, "com", account.Scope.ToString(),
                        "snapshot_unavailable", Array.Empty<DayEventDto>()));
                    continue;
                }

                var client = clients(account.Id);
                var canWrite = account.Scope == AccountScope.ReadWrite;
                var events = new List<DayEventDto>();
                foreach (var calendar in await client.ListCalendarsAsync(ct))
                {
                    foreach (var ev in await client.ListWindowAsync(calendar.Id, from, to, ct))
                    {
                        var replicas = linksBySource[ev.StableId]
                            .Select(l => new DayReplicaDto(
                                l.Id, l.DestinationAccountId, l.DestinationCalendarId,
                                l.MaskTitle, l.Status.ToString().ToLowerInvariant()))
                            .ToList();
                        events.Add(new DayEventDto(
                            account.Id, calendar.Id, ev.GraphEventId, ev.StableId, ev.Subject,
                            ev.Start, ev.End, ev.IsAllDay, ev.ShowAs, ev.IsCancelled,
                            ev.IsOrganizer,
                            // Either managed mark renders as replica — the UI must not offer
                            // re-replication (anti-loop 3 is also enforced server-side).
                            ev.HasReplicaMark || ev.HasCalImportMark,
                            canWrite, replicas));
                    }
                }
                accountViews.Add(new DayAccountDto(
                    account.Id, account.AccountEmail, "graph", account.Scope.ToString(),
                    "live", events.OrderBy(e => e.Start).ToList()));
            }

            return Results.Ok(new { date = day.ToString("yyyy-MM-dd"), accounts = accountViews });
        }).RequireIdentityBearer();
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

public sealed record RespondRequest(string? Action, string? Message, string? LinkId);

public sealed class RespondRequestValidator : AbstractValidator<RespondRequest>
{
    private static readonly string[] ValidActions = { "cancel", "accept", "decline", "tentative" };

    public RespondRequestValidator()
    {
        RuleFor(x => x.Action)
            .Must(a => !string.IsNullOrWhiteSpace(a) &&
                ValidActions.Contains(a.ToLowerInvariant()))
            .WithMessage("action must be one of: cancel, accept, decline, tentative.");
        RuleFor(x => x.Message).MaximumLength(1024);
    }
}

public sealed record DayReplicaDto(
    string LinkId, string DestinationAccountId, string DestinationCalendarId,
    string MaskTitle, string Status);

public sealed record DayEventDto(
    string AccountId, string CalendarId, string EventId, string StableId, string Title,
    DateTimeOffset Start, DateTimeOffset End, bool IsAllDay, string ShowAs, bool IsCancelled,
    bool IsOrganizer, bool IsReplica, bool CanWrite, IReadOnlyList<DayReplicaDto> Replicas);

public sealed record DayAccountDto(
    string AccountId, string Email, string Kind, string Scope, string Freshness,
    IReadOnlyList<DayEventDto> Events);
