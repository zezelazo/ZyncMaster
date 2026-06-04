namespace ZyncMaster.Server;

// The SINGLE place that knows both account representations exist at once (plan v2 §C-3):
//
//   * the NEW per-user pool (ICalendarAccountStore), keyed on a stable accountId, and
//   * the LEGACY single-account-per-UPN store (IConnectedAccountStore), keyed on the
//     account UPN ("" / "default" for the single-account case).
//
// The bridge is one-way and lazy ON READ (no startup backfill — §C-2): pair code asks for a
// CalendarAccount (or its refresh token) by accountId; if the id is not in the new pool the
// adapter derives the corresponding legacy account and wraps it under the SAME deterministic
// accountId so the rest of the system never has to care which store actually backs it.
//
// The deterministic id for a legacy account is UuidV5(AdapterNamespace, "{userId}|{accountRef}").
// The namespace is a FIXED project constant: changing it would make every legacy account's id
// drift and orphan the pairs that already reference it. Never change it.
//
// Retirement criterion: once no SyncPair endpoint references an account that resolves only
// through the legacy store (i.e. every endpoint's accountRef is a real pool accountId, and the
// legacy IConnectedAccountStore is empty for every user), this adapter and the legacy store can
// be deleted and the token providers can read ICalendarAccountStore directly.
public interface ILegacyConnectedAccountAdapter
{
    // The deterministic, stable accountId for a legacy account reference of the given user.
    // accountRef "" / null normalizes to the legacy "default" key before hashing, so the
    // single-account case maps to one stable id.
    string DeriveAccountId(string userId, string? accountRef);

    // Resolves a CalendarAccount by accountId for the current user. Looks in the new pool
    // first; if absent, derives it from the legacy store (wrapped under the same accountId).
    // Returns null when neither store has it.
    Task<CalendarAccount?> ResolveAsync(string accountId, CancellationToken ct = default);

    // Resolves the decrypted refresh token for an accountId. New pool first, then legacy.
    Task<string?> ResolveRefreshTokenAsync(string accountId, CancellationToken ct = default);

    // Persists a rotated refresh token for an accountId, writing it back to whichever store
    // actually backs the account (new pool if present, otherwise the legacy store). Keeps the
    // "single place that knows both representations" invariant: callers never branch on store.
    Task UpdateRefreshTokenAsync(string accountId, string refreshToken, CancellationToken ct = default);

    // Resolves an endpoint's account reference to a canonical pool accountId for the current
    // user. A reference is already an accountId when it resolves in the new pool; otherwise it
    // is treated as a legacy UPN ("" / "default" included) and the deterministic id is derived.
    // This is the one method pair code calls to migrate an Endpoint.AccountRef onto an accountId
    // while staying compatible with pairs that still carry a legacy UPN.
    Task<string> ResolveAccountIdAsync(string? accountRef, CancellationToken ct = default);
}
