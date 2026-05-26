using SyncMaster.Graph;

namespace SyncMaster.Server;

// Supplies Microsoft Graph access tokens on the server using the refresh token
// stored for the connected account. Unlike the desktop MSAL provider there is no
// interactive sign-in here: the account is connected once through the web flow and
// refreshed silently thereafter. Caches the bearer in memory until just before its
// expiry to avoid hitting the token endpoint on every Graph call.
public sealed class ServerGraphTokenProvider : IGraphTokenProvider
{
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);

    private readonly IMicrosoftTokenService _tokens;
    private readonly IConnectedAccountStore _accounts;
    private readonly string _userPrincipalName;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _expiresUtc;

    public ServerGraphTokenProvider(
        IMicrosoftTokenService tokens,
        IConnectedAccountStore accounts,
        string userPrincipalName)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _userPrincipalName = userPrincipalName ?? throw new ArgumentNullException(nameof(userPrincipalName));
    }

    public async Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && _cachedToken is not null && DateTimeOffset.UtcNow < _expiresUtc - ExpirySkew)
            return _cachedToken;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _cachedToken is not null && DateTimeOffset.UtcNow < _expiresUtc - ExpirySkew)
                return _cachedToken;

            var refreshToken = await _accounts.GetRefreshTokenAsync(_userPrincipalName, cancellationToken).ConfigureAwait(false);
            if (refreshToken is null)
                throw new AuthenticationFailedException("No Microsoft account is connected.");

            var result = await _tokens.RefreshAsync(refreshToken, cancellationToken).ConfigureAwait(false);

            _cachedToken = result.AccessToken;
            _expiresUtc = result.ExpiresUtc;

            if (!string.Equals(result.RefreshToken, refreshToken, StringComparison.Ordinal))
                await _accounts.SetAsync(_userPrincipalName, result.RefreshToken, cancellationToken).ConfigureAwait(false);

            return result.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }
}
