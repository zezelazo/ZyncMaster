using ZyncMaster.Graph;

namespace ZyncMaster.Server;

public sealed class PrefixEvaluationSummary
{
    public int RulesApplied { get; set; }
    public int ReplicasCreated { get; set; }
    public List<string> Failures { get; } = new();
}

// The "[Lunch] X" automation (spec §5). For each window event of a READWRITE Graph account:
//   1. strict match: subject starts with "[<prefix>]" (case-insensitive, first rule by
//      SortOrder wins, an event matches at most one rule);
//   2. the origin is renamed to the stripped "X" — the bracket is an instruction, not content
//      (empty rest falls back to the rule's mask, plan decision 5);
//   3. a replica titled MaskTitle fans out to EVERY destination of the rule (§3 invariants);
//   4. the origin is stamped ZmRuleProcessed = ruleId LAST, so an interrupted pass retries
//      and the link-store dedupe keeps the retry idempotent (exactly-once observable effect).
// The caller (ReplicaSyncRunner) only feeds events from readwrite Graph accounts: the rename
// and the stamp are WRITES on the origin account; read accounts are never half-evaluated and
// COM origins are explicitly out of scope in v1 (spec §5).
public sealed class PrefixRuleEvaluator
{
    private readonly ReplicaService _replicas;

    public PrefixRuleEvaluator(ReplicaService replicas)
        => _replicas = replicas ?? throw new ArgumentNullException(nameof(replicas));

    public static bool TryMatch(
        string subject, IReadOnlyList<PrefixRule> rules,
        out PrefixRule? rule, out string stripped)
    {
        rule = null;
        stripped = "";

        var s = (subject ?? "").TrimStart();
        if (!s.StartsWith('['))
            return false;
        var close = s.IndexOf(']');
        if (close <= 1)
            return false;

        var tag = s[1..close].Trim();
        foreach (var r in rules.Where(r => r.Enabled).OrderBy(r => r.SortOrder))
        {
            if (!string.Equals(tag, r.Prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            rule = r;
            stripped = s[(close + 1)..].Trim();
            if (stripped.Length == 0)
                stripped = r.MaskTitle; // "[Lunch]" with no rest: never leave an empty subject
            return true;
        }
        return false;
    }

    public async Task<PrefixEvaluationSummary> EvaluateAsync(
        string accountId,
        IReadOnlyList<SourceEventSnapshot> events,
        IReadOnlyList<PrefixRule> rules,
        IReplicaGraphClient originClient,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(originClient);

        var summary = new PrefixEvaluationSummary();
        foreach (var ev in events)
        {
            ct.ThrowIfCancellationRequested();

            if (ev.IsCancelled)
                continue;
            // Anti-loop 1 (spec §7): an event created by EITHER engine never matches a rule.
            if (ev.HasReplicaMark || ev.HasCalImportMark)
                continue;
            // Anti-loop 2: the stamp makes strip+fan-out happen exactly once, no matter how
            // many polls re-see the event.
            if (ev.RuleProcessedBy.Length > 0)
                continue;
            if (!TryMatch(ev.Subject, rules, out var rule, out var stripped))
                continue;

            try
            {
                // 1) Strip: the bracket is an instruction, not content. Visible and reversible
                //    in the USER's own origin calendar (spec §14 mitigation).
                await originClient.UpdateSubjectAsync(ev.GraphEventId, stripped, ct);

                // 2) Fan-out with the rule's mask to every destination of the rule. The
                //    link-store dedupe makes a crashed-pass retry idempotent.
                var destinations = rule!.Destinations
                    .Select(d => new ReplicaDestinationRequest(d.AccountId, d.CalendarId, rule.MaskTitle))
                    .ToList();
                var outcome = await _replicas.FanOutFromSnapshotAsync(
                    accountId, ev, destinations, rule.Id, ct);
                if (outcome.ErrorCode is not null)
                {
                    summary.Failures.Add(
                        $"Rule '{rule.Id}' fan-out rejected for event '{ev.GraphEventId}': {outcome.ErrorCode}");
                    continue; // do NOT stamp: the next poll retries the fan-out
                }
                summary.ReplicasCreated += outcome.Created.Count;
                summary.Failures.AddRange(outcome.Failures);

                // 3) Stamp LAST: only a pass that renamed and fanned out marks the event done.
                await originClient.StampRuleProcessedAsync(ev.GraphEventId, rule.Id, ct);
                summary.RulesApplied++;
            }
            catch (Exception ex)
            {
                // Best-effort per event: one failure must not abort the account's pass.
                summary.Failures.Add(
                    $"Rule evaluation failed for event '{ev.GraphEventId}': {ex.Message}");
            }
        }
        return summary;
    }
}
