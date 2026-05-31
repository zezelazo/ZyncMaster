namespace ZyncMaster.Server;

public interface IMicrosoftTokenService
{
    Task<TokenResult> ExchangeCodeAsync(string code, CancellationToken ct = default);
    Task<TokenResult> RefreshAsync(string refreshToken, CancellationToken ct = default);

    // Identity-only code exchange: uses ServerOptions.IdentityScopes (openid email profile)
    // and ServerOptions.IdentityRedirectUri. We only need the id_token identity claims
    // (subject/email/displayName) — the calendar refresh token is intentionally NOT used or
    // stored here. This is sign-in, not calendar connection.
    Task<TokenResult> ExchangeIdentityCodeAsync(string code, CancellationToken ct = default);

    // Calendar-connect code exchange (Track A-2): exchanges the authorization code against the
    // calendar redirect URI requesting the EXACT scopes the user consented to (read or
    // read/write — the caller passes ServerOptions.CalendarReadScopes /
    // CalendarReadWriteScopes). Unlike ExchangeIdentityCodeAsync the result IS persisted: the
    // returned refresh token is stored encrypted on the CalendarAccount and the id_token email
    // becomes the account email. Distinct from ExchangeCodeAsync which uses the legacy
    // single-user RedirectUri/Scopes.
    Task<TokenResult> ExchangeCalendarCodeAsync(string code, string scopes, CancellationToken ct = default);
}
