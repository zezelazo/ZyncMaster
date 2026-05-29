namespace ZyncMaster.Server;

public interface IMicrosoftTokenService
{
    Task<TokenResult> ExchangeCodeAsync(string code, CancellationToken ct = default);
    Task<TokenResult> RefreshAsync(string refreshToken, CancellationToken ct = default);
}
