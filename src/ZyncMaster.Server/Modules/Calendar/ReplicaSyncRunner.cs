using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZyncMaster.Graph;
using ZyncMaster.Server.Data;

namespace ZyncMaster.Server;

public sealed class ReplicaRunSummary
{
    public int UsersProcessed { get; set; }
    public int RulesApplied { get; set; }
    public int ReplicasCreated { get; set; }
    public int Moved { get; set; }
    public int Cancelled { get; set; }
    public int Broken { get; set; }
    public int Failed { get; set; }
}

// Cross-user replica reconciliation + prefix-rule pass, driven by the SAME external trigger as
// the pair mirror (/api/sync/run-due — the VPS crontab). Pattern copied from CronSyncRunner:
// the due users are found with direct cross-user queries, then each user is processed under
// their own identity via IHttpCurrentUserOverride so the user-scoped stores and the per-account
// token resolution work downstream. Unlike pairs, the device lease does NOT gate this runner:
// the App never executes the replica engine (it is Graph-only, server-side by design).
//
// Per link (spec §7):
//   origin moved      -> PATCH times/showAs on the replica, SKIPPED when ContentHash matches;
//   origin cancelled  -> DELETE the replica + link to tombstone (clean & silent, no attendees);
//   replica deleted by hand -> link to BROKEN (a user decision point: recreate / discard /
//                        write-back — the runner NEVER resolves it silently). Detection uses
//                        ONE paginated ZmReplicaOf read per destination calendar, not N GETs.
// SourceKind == "com" links are reserved (v1.1 snapshot work) and skipped untouched.
public sealed class ReplicaSyncRunner
{
    private readonly IDbContextFactory<ZyncMasterDbContext> _factory;
    private readonly IHttpCurrentUserOverride _userOverride;
    private readonly ICalendarAccountStore _accounts;
    private readonly IReplicaLinkStore _links;
    private readonly IPrefixRuleStore _rules;
    private readonly Func<string, IReplicaGraphClient> _clients;
    private readonly ReplicaService _replicas;
    private readonly PrefixRuleEvaluator _evaluator;
    private readonly ServerOptions _options;
    private readonly ILogger<ReplicaSyncRunner> _logger;
    private readonly ReplicaDraftBuilder _draftBuilder = new();

    public ReplicaSyncRunner(
        IDbContextFactory<ZyncMasterDbContext> factory,
        IHttpCurrentUserOverride userOverride,
        ICalendarAccountStore accounts,
        IReplicaLinkStore links,
        IPrefixRuleStore rules,
        Func<string, IReplicaGraphClient> clients,
        ReplicaService replicas,
        PrefixRuleEvaluator evaluator,
        IOptions<ServerOptions> options,
        ILogger<ReplicaSyncRunner> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _userOverride = userOverride ?? throw new ArgumentNullException(nameof(userOverride));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _links = links ?? throw new ArgumentNullException(nameof(links));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _replicas = replicas ?? throw new ArgumentNullException(nameof(replicas));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ReplicaRunSummary> RunAsync(CancellationToken ct = default)
    {
        var summary = new ReplicaRunSummary();

        // Cross-user discovery: any user with a live (non-tombstone) link or an enabled rule.
        List<string> users;
        await using (var db = await _factory.CreateDbContextAsync(ct))
        {
            var linkUsers = await db.ReplicaLinks.AsNoTracking()
                .Where(l => l.Status != "tombstone")
                .Select(l => l.UserId).Distinct().ToListAsync(ct);
            var ruleUsers = await db.PrefixRules.AsNoTracking()
                .Where(r => r.Enabled)
                .Select(r => r.UserId).Distinct().ToListAsync(ct);
            users = linkUsers.Union(ruleUsers, StringComparer.Ordinal).ToList();
        }

        foreach (var userId in users)
        {
            ct.ThrowIfCancellationRequested();
            _userOverride.Set(userId);
            try
            {
                await RunForUserAsync(summary, ct);
                summary.UsersProcessed++;
            }
            catch (Exception ex)
            {
                // Best-effort per user: one user's broken account must not stall everyone.
                summary.Failed++;
                _logger.LogWarning(ex, "Replica run failed for user {UserId}", userId);
            }
            finally
            {
                _userOverride.Clear();
            }
        }

        _logger.LogInformation(
            "Replica run summary: users={Users} rules={Rules} created={Created} moved={Moved} " +
            "cancelled={Cancelled} broken={Broken} failed={Failed}.",
            summary.UsersProcessed, summary.RulesApplied, summary.ReplicasCreated,
            summary.Moved, summary.Cancelled, summary.Broken, summary.Failed);
        return summary;
    }

    private async Task RunForUserAsync(ReplicaRunSummary summary, CancellationToken ct)
    {
        var accounts = (await _accounts.ListAsync(ct))
            .Where(a => a.Kind == AccountKind.Graph && a.Status == "active")
            .ToDictionary(a => a.Id, StringComparer.Ordinal);
        var (from, to) = Window();

        // 1) Prefix rules — READWRITE Graph accounts only: the strip and the stamp are writes
        //    on the origin (spec §5); read accounts are skipped whole, COM origins are v1.1.
        var rules = (await _rules.ListAsync(ct)).Where(r => r.Enabled).ToList();
        if (rules.Count > 0)
        {
            foreach (var account in accounts.Values.Where(a => a.Scope == AccountScope.ReadWrite))
            {
                var client = _clients(account.Id);
                foreach (var calendar in await client.ListCalendarsAsync(ct))
                {
                    var events = await client.ListWindowAsync(calendar.Id, from, to, ct);
                    var s = await _evaluator.EvaluateAsync(account.Id, events, rules, client, ct);
                    summary.RulesApplied += s.RulesApplied;
                    summary.ReplicasCreated += s.ReplicasCreated;
                    foreach (var failure in s.Failures)
                        _logger.LogWarning("Prefix rule failure: {Failure}", failure);
                }
            }
        }

        // 2) Link reconciliation — graph links only (com is reserved for the snapshot work).
        var links = (await _links.ListAsync(ct))
            .Where(l => l.Status == ReplicaLinkStatus.Active && l.SourceKind == "graph")
            .ToList();
        if (links.Count == 0)
            return;

        // ONE paginated read per destination calendar (the replica engine's answer to the
        // pair mirror's N+1 finding): the set of OUR replicas still present in the window.
        var present = new Dictionary<(string AccountId, string CalendarId), HashSet<string>>();
        foreach (var group in links.GroupBy(l => (l.DestinationAccountId, l.DestinationCalendarId)))
        {
            var refs = await _clients(group.Key.DestinationAccountId)
                .ListReplicasInWindowAsync(group.Key.DestinationCalendarId, from, to, ct);
            present[group.Key] = refs.Select(r => r.EventId).ToHashSet(StringComparer.Ordinal);
        }

        foreach (var link in links)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (link.SourceAccountId is null || !accounts.ContainsKey(link.SourceAccountId))
                    continue; // source account disconnected: leave the link visible, never guess

                var snapshot = await _clients(link.SourceAccountId)
                    .GetEventAsync(link.SourceGraphEventId, ct);
                var destClient = _clients(link.DestinationAccountId);
                var now = DateTimeOffset.UtcNow;

                if (snapshot is null || snapshot.IsCancelled)
                {
                    // Origin cancelled/deleted -> DELETE the replica (clean & silent: replicas
                    // have no attendees) + close the link (spec §3/§7).
                    await destClient.DeleteEventAsync(link.DestinationEventId, ct);
                    await _links.UpdateAsync(link with
                    {
                        Status = ReplicaLinkStatus.Tombstone, UpdatedUtc = now,
                    }, ct);
                    summary.Cancelled++;
                    continue;
                }

                // Replica deleted by hand at the destination -> BROKEN. Only checked when the
                // source still falls inside the scanned window (outside it, absence from the
                // window list proves nothing). Drift rule: states are made visible, the runner
                // never auto-deletes nor auto-recreates on doubt (spec §14).
                var inWindow = snapshot.Start < to && snapshot.End > from;
                if (inWindow &&
                    present.TryGetValue((link.DestinationAccountId, link.DestinationCalendarId), out var ids) &&
                    !ids.Contains(link.DestinationEventId))
                {
                    await _links.UpdateAsync(link with
                    {
                        Status = ReplicaLinkStatus.Broken, UpdatedUtc = now,
                    }, ct);
                    summary.Broken++;
                    continue;
                }

                // Origin moved -> propagate times/showAs ONLY, skip-by-hash. The mask title is
                // NEVER touched on updates — it belongs to the user (spec §3).
                var hash = ReplicaContentHash.For(
                    snapshot.Start, snapshot.End, snapshot.ShowAs, snapshot.IsAllDay);
                if (!string.Equals(hash, link.ContentHash, StringComparison.Ordinal))
                {
                    var draft = _draftBuilder.Build(snapshot, link.MaskTitle);
                    await destClient.UpdateReplicaTimesAsync(link.DestinationEventId, draft, ct);
                    await _links.UpdateAsync(link with { ContentHash = hash, UpdatedUtc = now }, ct);
                    summary.Moved++;
                }
            }
            catch (Exception ex)
            {
                summary.Failed++;
                _logger.LogWarning(ex, "Replica reconcile failed for link {LinkId}", link.Id);
            }
        }
    }

    // Same window as the pair cron: [today 00:00Z, +SyncWindowDays].
    private (DateTimeOffset From, DateTimeOffset To) Window()
    {
        var today = DateTime.UtcNow.Date;
        var from = new DateTimeOffset(today, TimeSpan.Zero);
        return (from, from.AddDays(_options.SyncWindowDays <= 0 ? 14 : _options.SyncWindowDays));
    }
}
