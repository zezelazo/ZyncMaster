using ZyncMaster.Graph;
using ZyncMaster.Server.Modules.Calendar;

namespace ZyncMaster.Server;

// Server-side Graph token provider keyed on the pool accountId (plan v2 §C-5). Functionally
// identical to ServerGraphTokenProvider — silent refresh of a stored refresh token, in-memory
// caching of the bearer until just before expiry — but it resolves the refresh token by
// accountId through the legacy adapter instead of by UPN. The adapter bridges to the legacy
// single-account store transparently, so a pair that still references a legacy account keeps
// syncing through this provider without any data migration.
//
// It is constructed with the endpoint's raw account reference (which may be a real pool
// accountId or a legacy UPN). The reference is resolved to a canonical accountId lazily, on the
// first token fetch, because the resolution reads the current user + a store and the factory
// that builds this provider is synchronous. The IGraphTokenProvider contract is unchanged; only
// the SOURCE of the refresh token differs.
public sealed class AccountAwareGraphTokenProvider : IGraphTokenProvider
{
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);

    private readonly IMicrosoftTokenService _tokens;
    private readonly ILegacyConnectedAccountAdapter _adapter;
    private readonly string? _accountRef;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _resolvedAccountId;
    private string? _cachedToken;
    private DateTimeOffset _expiresUtc;

    public AccountAwareGraphTokenProvider(
        IMicrosoftTokenService tokens,
        ILegacyConnectedAccountAdapter adapter,
        string? accountRef)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _accountRef = accountRef;
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

            var accountId = _resolvedAccountId ??=
                await _adapter.ResolveAccountIdAsync(_accountRef, cancellationToken).ConfigureAwait(false);

            var refreshToken = await _adapter.ResolveRefreshTokenAsync(accountId, cancellationToken).ConfigureAwait(false);
            if (refreshToken is null)
                throw new AuthenticationFailedException("No Microsoft account is connected for this account id.");

            var result = await _tokens.RefreshAsync(refreshToken, cancellationToken).ConfigureAwait(false);

            _cachedToken = result.AccessToken;
            _expiresUtc = result.ExpiresUtc;

            // Rotate the stored refresh token back to whichever store backs this account.
            if (!string.Equals(result.RefreshToken, refreshToken, StringComparison.Ordinal))
                await _adapter.UpdateRefreshTokenAsync(accountId, result.RefreshToken, cancellationToken).ConfigureAwait(false);

            return result.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }
}
