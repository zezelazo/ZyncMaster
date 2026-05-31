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
}
