using ZyncMaster.Graph;

namespace ZyncMaster.Server;

public sealed record ReplicaDestinationRequest(string AccountId, string CalendarId, string Title);

// Outcome of a replica operation. ErrorCode != null means the call was rejected BEFORE any
// creation (validation/anti-loop); Created/Failures describe a per-destination best-effort run.
public sealed record FanOutOutcome
{
    public IReadOnlyList<ReplicaLink> Created { get; init; } = Array.Empty<ReplicaLink>();
    public IReadOnlyList<string> Failures { get; init; } = Array.Empty<string>();
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public static FanOutOutcome Error(string code, string message) =>
        new() { ErrorCode = code, ErrorMessage = message };
}

// The replica engine's write side (spec §3/§7): manual fan-out, rule fan-out (via the snapshot
// overload), mask-title edits, recreate-after-broken and remove/discard. Owns the three hard
// anti-loop rules together with PrefixRuleEvaluator:
//   (1) an event carrying ZmReplicaOf or CalImportSourceId is NEVER a source (checked here);
//   (2) prefix rules fire once per event (the evaluator checks the stamp);
//   (3) a replica cannot be replicated (same check as (1) — replicas always carry the mark).
public sealed class ReplicaService
{
    private readonly ICalendarAccountStore _accounts;
    private readonly IReplicaLinkStore _links;
    private readonly Func<string, IReplicaGraphClient> _clients;
    private readonly TimeProvider _clock;
    private readonly ReplicaDraftBuilder _draftBuilder = new();

    public ReplicaService(
        ICalendarAccountStore accounts,
        IReplicaLinkStore links,
        Func<string, IReplicaGraphClient> clients,
        TimeProvider clock)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _links = links ?? throw new ArgumentNullException(nameof(links));
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<FanOutOutcome> FanOutAsync(
        string sourceAccountId,
        string sourceGraphEventId,
        IReadOnlyList<ReplicaDestinationRequest> destinations,
        string? ruleId,
        CancellationToken ct = default)
    {
        var sourceAccount = await _accounts.GetAsync(sourceAccountId, ct);
        if (sourceAccount is null)
            return FanOutOutcome.Error("source_account_not_found", "Unknown source account.");
        if (sourceAccount.Kind != AccountKind.Graph)
            return FanOutOutcome.Error("com_source_not_supported",
                "v1 replicates Graph origins only; COM origins arrive with the snapshot ingestion (spec §13).");

        var snapshot = await _clients(sourceAccountId).GetEventAsync(sourceGraphEventId, ct);
        if (snapshot is null)
            return FanOutOutcome.Error("source_event_not_found", "The source event no longer exists.");

        return await FanOutFromSnapshotAsync(sourceAccountId, snapshot, destinations, ruleId, ct);
    }

    // Snapshot overload: the prefix evaluator and replicate-on-create already hold the event.
    public async Task<FanOutOutcome> FanOutFromSnapshotAsync(
        string sourceAccountId,
        SourceEventSnapshot snapshot,
        IReadOnlyList<ReplicaDestinationRequest> destinations,
        string? ruleId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(destinations);

        // ANTI-LOOP (rules 1 and 3 of spec §7): an event created by EITHER engine is never a
        // replication source. This is the contract test of the general design's cascade risk.
        if (snapshot.HasReplicaMark || snapshot.HasCalImportMark)
            return FanOutOutcome.Error("replica_cannot_be_source",
                "Events created by the replica engine or the pair mirror are never replication sources.");
        if (snapshot.IsCancelled)
            return FanOutOutcome.Error("source_event_cancelled", "A cancelled event cannot be replicated.");

        // Validate EVERY destination before creating anything (no partial surprises on bad input).
        foreach (var d in destinations)
        {
            if (string.IsNullOrWhiteSpace(d.Title))
                return FanOutOutcome.Error("mask_title_required",
                    $"Destination calendar '{d.CalendarId}' needs a manual title — it never defaults to the source subject.");
            var account = await _accounts.GetAsync(d.AccountId, ct);
            if (account is null)
                return FanOutOutcome.Error("destination_account_not_found", $"Unknown account '{d.AccountId}'.");
            if (account.Kind != AccountKind.Graph)
                return FanOutOutcome.Error("destination_not_graph", "Replicas can only be written to Graph accounts.");
            if (account.Scope != AccountScope.ReadWrite)
                return FanOutOutcome.Error("readwrite_scope_required",
                    $"Account '{d.AccountId}' is read-only; upgrade its scope to replicate into it.");
        }

        var existing = await _links.ListBySourceEventAsync(snapshot.StableId, ct);
        var hash = ReplicaContentHash.For(snapshot.Start, snapshot.End, snapshot.ShowAs, snapshot.IsAllDay);
        var created = new List<ReplicaLink>();
        var failures = new List<string>();

        foreach (var d in destinations)
        {
            // Idempotency: re-running a fan-out (a crashed rule pass, a double click) never
            // duplicates a destination that already has an ACTIVE link for this source.
            var duplicate = existing.Any(l =>
                l.Status == ReplicaLinkStatus.Active &&
                l.DestinationAccountId == d.AccountId &&
                l.DestinationCalendarId == d.CalendarId);
            if (duplicate)
                continue;

            try
            {
                var draft = _draftBuilder.Build(snapshot, d.Title);
                var eventId = await _clients(d.AccountId).CreateReplicaAsync(d.CalendarId, draft, ct);
                var now = _clock.GetUtcNow();
                var link = await _links.AddAsync(new ReplicaLink
                {
                    Id = Guid.NewGuid().ToString("N"),
                    SourceAccountId = sourceAccountId,
                    SourceEventId = snapshot.StableId,
                    SourceGraphEventId = snapshot.GraphEventId,
                    SourceKind = "graph",
                    DestinationAccountId = d.AccountId,
                    DestinationCalendarId = d.CalendarId,
                    DestinationEventId = eventId,
                    MaskTitle = draft.MaskTitle,
                    RuleId = ruleId,
                    ContentHash = hash,
                    Status = ReplicaLinkStatus.Active,
                    CreatedUtc = now,
                    UpdatedUtc = now,
                }, ct);
                created.Add(link);
            }
            catch (Exception ex)
            {
                // Best-effort per destination: one failed calendar must not abort its siblings.
                failures.Add($"Replica create failed for calendar '{d.CalendarId}': {ex.Message}");
            }
        }

        return new FanOutOutcome { Created = created, Failures = failures };
    }

    // Edit the manual mask title of an ACTIVE replica (spec §3: "editar una réplica desde
    // nuestra UI = editar su título manual o quitarla").
    public async Task<FanOutOutcome> UpdateTitleAsync(string linkId, string title, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return FanOutOutcome.Error("mask_title_required", "The mask title cannot be empty.");

        var link = await _links.GetAsync(linkId, ct);
        if (link is null)
            return FanOutOutcome.Error("link_not_found", "Unknown replica link.");
        if (link.Status != ReplicaLinkStatus.Active)
            return FanOutOutcome.Error("link_not_active", "Only an active replica's title can be edited.");

        await _clients(link.DestinationAccountId).UpdateSubjectAsync(link.DestinationEventId, title.Trim(), ct);
        await _links.UpdateAsync(link with { MaskTitle = title.Trim(), UpdatedUtc = _clock.GetUtcNow() }, ct);
        return new FanOutOutcome();
    }

    // Recreate a BROKEN replica (exit 1 of the broken-link flow, spec §3): a fresh whitelist
    // event is created at the same destination and the link returns to active.
    public async Task<FanOutOutcome> RecreateAsync(string linkId, CancellationToken ct = default)
    {
        var link = await _links.GetAsync(linkId, ct);
        if (link is null)
            return FanOutOutcome.Error("link_not_found", "Unknown replica link.");
        if (link.Status != ReplicaLinkStatus.Broken)
            return FanOutOutcome.Error("link_not_broken", "Only a broken link can be recreated.");
        if (link.SourceAccountId is null)
            return FanOutOutcome.Error("com_source_not_supported", "COM-source links cannot be recreated in v1.");

        var snapshot = await _clients(link.SourceAccountId).GetEventAsync(link.SourceGraphEventId, ct);
        if (snapshot is null || snapshot.IsCancelled)
            return FanOutOutcome.Error("source_event_not_found",
                "The origin no longer exists — discard the link or write back instead.");

        var draft = _draftBuilder.Build(snapshot, link.MaskTitle);
        var eventId = await _clients(link.DestinationAccountId)
            .CreateReplicaAsync(link.DestinationCalendarId, draft, ct);
        var updated = link with
        {
            DestinationEventId = eventId,
            ContentHash = ReplicaContentHash.For(snapshot.Start, snapshot.End, snapshot.ShowAs, snapshot.IsAllDay),
            Status = ReplicaLinkStatus.Active,
            UpdatedUtc = _clock.GetUtcNow(),
        };
        await _links.UpdateAsync(updated, ct);
        return new FanOutOutcome { Created = new[] { updated } };
    }

    // Remove a replica (DELETE endpoint) — also the "discard" exit of a broken link: an active
    // link deletes the remote event first; a broken one has nothing left to delete in Graph.
    public async Task<FanOutOutcome> RemoveAsync(string linkId, CancellationToken ct = default)
    {
        var link = await _links.GetAsync(linkId, ct);
        if (link is null)
            return FanOutOutcome.Error("link_not_found", "Unknown replica link.");
        if (link.Status == ReplicaLinkStatus.Tombstone)
            return new FanOutOutcome(); // already closed — removing twice is a no-op

        if (link.Status == ReplicaLinkStatus.Active)
            await _clients(link.DestinationAccountId).DeleteEventAsync(link.DestinationEventId, ct);

        await _links.UpdateAsync(link with
        {
            Status = ReplicaLinkStatus.Tombstone,
            UpdatedUtc = _clock.GetUtcNow(),
        }, ct);
        return new FanOutOutcome();
    }
}
