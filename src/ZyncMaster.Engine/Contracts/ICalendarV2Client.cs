using System.Threading;
using System.Threading.Tasks;

namespace ZyncMaster.Engine;

// REST client for the server's Calendar v2 management surface (unified day view, event-level
// replicas, prefix rules). Human-only: every call carries the signed-in user's IDENTITY BEARER,
// mirroring the accounts/pairs management calls in IPairsClient. Payloads travel as RAW JSON
// strings in both directions — the server owns the wire shape and the UIs render it directly,
// so the App never re-models these DTOs (single source of truth: the server contract).
public interface ICalendarV2Client
{
    // GET /api/calendar/day?date=yyyy-MM-dd → unified day JSON (accounts + events + replicas).
    Task<string> GetDayAsync(string bearer, string dateIso, CancellationToken ct);

    // POST /api/calendar/events (body: origin event + optional replicas[]) → {eventId, replicas} JSON.
    Task<string> CreateEventAsync(string bearer, string requestJson, CancellationToken ct);

    // POST /api/calendar/events/{accountId}/{eventId}/replicas (body: {destinations:[...]}) →
    // fan-out result JSON. Two route segments — backend plan decision 1, never a single ref.
    Task<string> CreateReplicasAsync(string bearer, string accountId, string eventId, string requestJson, CancellationToken ct);

    // GET /api/calendar/prefix-rules → rules array JSON.
    Task<string> ListPrefixRulesAsync(string bearer, CancellationToken ct);

    // POST /api/calendar/prefix-rules → created rule JSON.
    Task<string> CreatePrefixRuleAsync(string bearer, string ruleJson, CancellationToken ct);

    // PUT /api/calendar/prefix-rules/{id} → updated rule JSON.
    Task<string> UpdatePrefixRuleAsync(string bearer, string ruleId, string ruleJson, CancellationToken ct);

    // DELETE /api/calendar/prefix-rules/{id}.
    Task DeletePrefixRuleAsync(string bearer, string ruleId, CancellationToken ct);
}
