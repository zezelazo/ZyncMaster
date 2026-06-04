namespace ZyncMaster.Server;

// Best-effort backfill of a connected calendar account's mailbox email + display name for
// accounts that were connected BEFORE the /me capture existed (their AccountEmail is blank, so the
// UI would otherwise fall back to showing the internal accountRef GUID). On a listing request, for
// each Graph account whose email is still empty, we:
//   1. fetch its stored refresh token,
//   2. mint a fresh access token via the Microsoft token service,
//   3. call Graph /me to read mail/userPrincipalName/displayName,
//   4. PERSIST the result on the account and return the enriched record.
//
// Everything is best-effort and side-effect-tolerant: any failure (no token, refresh fails, /me
// fails) leaves the account unchanged and returns it as-is, so the listing never breaks. Accounts
// that already have an email are returned untouched (no /me, no token spend), so the cost is paid
// at most ONCE per account — the first listing after which the email is persisted.
public sealed class CalendarAccountEmailBackfill
{
    private readonly ICalendarAccountStore _accounts;
    private readonly IMicrosoftTokenService _tokens;
    private readonly IGraphUserInfoService _userInfo;

    public CalendarAccountEmailBackfill(
        ICalendarAccountStore accounts,
        IMicrosoftTokenService tokens,
        IGraphUserInfoService userInfo)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _userInfo = userInfo ?? throw new ArgumentNullException(nameof(userInfo));
    }

    // Returns the account enriched with a real email/displayName when a backfill succeeded,
    // otherwise the account unchanged. A non-Graph account, an account that already has an email,
    // or any failure all return the original record.
    public async Task<CalendarAccount> EnsureEmailAsync(CalendarAccount account, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        // Only Graph accounts have a refresh token to mint an access token for /me. OutlookCom
        // accounts are device-backed (no server token) and keep their device name as display.
        if (account.Kind != AccountKind.Graph)
            return account;

        // Already named — nothing to do, and crucially no /me per request once captured.
        if (!string.IsNullOrWhiteSpace(account.AccountEmail))
            return account;

        try
        {
            var refreshToken = await _accounts.GetRefreshTokenAsync(account.Id, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(refreshToken))
                return account;

            var token = await _tokens.RefreshAsync(refreshToken, ct).ConfigureAwait(false);

            // Rotate the stored refresh token if Microsoft returned a new one, so a one-time
            // rotating token is not lost on the next run.
            if (!string.IsNullOrEmpty(token.RefreshToken) &&
                !string.Equals(token.RefreshToken, refreshToken, StringComparison.Ordinal))
            {
                await _accounts.UpdateRefreshTokenAsync(account.Id, token.RefreshToken, ct).ConfigureAwait(false);
            }

            var me = await _userInfo.GetMeAsync(token.AccessToken, ct).ConfigureAwait(false);
            if (!me.HasEmail && string.IsNullOrWhiteSpace(me.DisplayName))
                return account;

            await _accounts.UpdateProfileAsync(account.Id, me.Email, me.DisplayName, ct).ConfigureAwait(false);

            // Reflect the persisted values in the record we return so the SAME response shows the
            // real email without a second round-trip.
            return account with
            {
                AccountEmail = me.HasEmail ? me.Email : account.AccountEmail,
                DisplayName = !string.IsNullOrWhiteSpace(me.DisplayName) ? me.DisplayName : account.DisplayName,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: a refresh/token failure must never break the listing. Fall back to the
            // account as-is (the UI then shows a dignified generic label, not the GUID).
            return account;
        }
    }
}
