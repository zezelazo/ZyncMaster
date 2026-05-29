using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;
using ZyncMaster.Graph;

namespace ZyncMaster.CalImport;

public sealed class GraphAuthenticator : IGraphTokenProvider
{
    private const           string   CacheFileName = "msal.cache";
    private static readonly string[] Scopes        = new[]
    {
        "https://graph.microsoft.com/Calendars.ReadWrite",
        "https://graph.microsoft.com/User.Read",
    };

    private readonly IPublicClientApplication _app;
    private readonly string?                  _accountHint;
    private readonly string                   _cacheDirectory;
    private readonly SemaphoreSlim            _initLock = new SemaphoreSlim(1, 1);
    private volatile bool                     _cacheInitialized;

    public GraphAuthenticator(string clientId, string authority, string? accountHint, string cacheDirectory)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("clientId is required.", nameof(clientId));
        if (string.IsNullOrWhiteSpace(authority))
            throw new ArgumentException("authority is required.", nameof(authority));
        if (string.IsNullOrWhiteSpace(cacheDirectory))
            throw new ArgumentException("cacheDirectory is required.", nameof(cacheDirectory));

        _app = PublicClientApplicationBuilder
            .Create(clientId)
            .WithAuthority(authority)
            .WithRedirectUri("http://localhost")
            .Build();

        _accountHint    = accountHint;
        _cacheDirectory = cacheDirectory;
    }

    public async Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        await EnsureCacheInitializedAsync(cancellationToken).ConfigureAwait(false);

        var accounts = await _app.GetAccountsAsync().ConfigureAwait(false);
        IAccount? account = string.IsNullOrEmpty(_accountHint)
            ? accounts.FirstOrDefault()
            : accounts.FirstOrDefault(a => string.Equals(a.Username, _accountHint, StringComparison.OrdinalIgnoreCase))
              ?? accounts.FirstOrDefault();

        try
        {
            // WithForceRefresh skips MSAL's in-memory access-token cache and goes to the
            // token endpoint using the refresh token. This is what we need after a 401 —
            // a plain AcquireTokenSilent would just hand back the same expired bearer.
            var silent = await _app
                .AcquireTokenSilent(Scopes, account)
                .WithForceRefresh(forceRefresh)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
            return silent.AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            return await AcquireInteractiveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (MsalServiceException ex) when (ex.ErrorCode == "invalid_grant")
        {
            return await AcquireInteractiveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (MsalException ex)
        {
            // Surface as AuthenticationFailedException so ApplicationRunner can distinguish
            // a fatal auth problem from a per-item Graph error and abort the run.
            throw new AuthenticationFailedException(
                "Authentication failed. If this persists, delete the cache at %LOCALAPPDATA%\\ZyncMaster\\CalImport\\msal.cache and re-run.",
                ex);
        }
    }

    private async Task<string> AcquireInteractiveAsync(CancellationToken cancellationToken)
    {
        var builder = _app.AcquireTokenInteractive(Scopes).WithUseEmbeddedWebView(false);
        if (!string.IsNullOrEmpty(_accountHint))
            builder = builder.WithLoginHint(_accountHint);

        try
        {
            var interactive = await builder.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return interactive.AccessToken;
        }
        catch (MsalClientException ex) when (ex.ErrorCode == MsalError.AuthenticationCanceledError)
        {
            throw new AuthenticationFailedException("Sign-in cancelled by user.", ex);
        }
        catch (MsalException ex)
        {
            throw new AuthenticationFailedException(
                "Authentication failed. If this persists, delete the cache at %LOCALAPPDATA%\\ZyncMaster\\CalImport\\msal.cache and re-run.",
                ex);
        }
    }

    private async Task EnsureCacheInitializedAsync(CancellationToken ct)
    {
        if (_cacheInitialized) return;
        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cacheInitialized) return;

            Directory.CreateDirectory(_cacheDirectory);

            // On Windows MsalCacheHelper encrypts the cache with DPAPI automatically.
            var props  = new StorageCreationPropertiesBuilder(CacheFileName, _cacheDirectory).Build();
            var helper = await MsalCacheHelper.CreateAsync(props).ConfigureAwait(false);
            helper.RegisterCache(_app.UserTokenCache);

            _cacheInitialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }
}
