using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ZyncMaster.App.Bridge;

// A connected calendar account as the Server reports it from GET /api/calendar/accounts. A thin
// DTO surfaced to the UI so it can render the freshly connected account without a calendar token
// (the refresh token never leaves the Server). Mirrors the JSON shape the endpoint emits.
public sealed record CalendarAccountSummary(
    string Id,
    string Kind,
    string Provider,
    string AccountEmail,
    string Scope,
    string Status,
    string DisplayName);

// The App-side HTTP surface against the Server's calendar-account endpoints (Track A-2). These are
// gated by the IdentityBearer scheme — the App sends the signed-in user's identity access token as
// the bearer, NEVER the device api key (which is a different surface). Abstracted so the
// CalendarConnectService can be unit-tested with a fake; the real impl is a thin HttpClient wrapper,
// untested like the other infrastructure clients per CLAUDE.md.
public interface ICalendarServerClient
{
    // POST /api/calendar/connect/graph/start with { scope, port, nonce } and the identity access
    // token as Bearer. Returns the Microsoft authorize URL to open in the system browser, or null
    // when the Server rejects the request (e.g. the bearer is missing/expired -> 401).
    Task<string?> StartGraphConnectAsync(
        string accessToken, string scope, int port, string nonce, CancellationToken ct = default);

    // POST /api/calendar/accounts/{id}/upgrade-scope with { port, nonce } and the identity access
    // token as Bearer. Returns the Microsoft authorize URL to open in the system browser to grant
    // read/write on an already-connected (read-only) account, or null when the Server rejects it.
    Task<string?> UpgradeAccountScopeAsync(
        string accessToken, string accountId, int port, string nonce, CancellationToken ct = default);

    // GET /api/calendar/accounts with the identity access token as Bearer. Returns the caller's
    // connected calendar accounts (never the refresh token), or an empty list on a non-2xx.
    Task<IReadOnlyList<CalendarAccountSummary>> ListCalendarAccountsAsync(
        string accessToken, CancellationToken ct = default);
}
