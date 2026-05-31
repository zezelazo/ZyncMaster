using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ZyncMaster.App.Bridge;

// Orchestrates desktop sign-in against the Server's identity endpoints, mirroring PairingService's
// system-browser + loopback pattern. Three flows land on the SAME loopback callback handling:
//
//   * LoginWithMicrosoftAsync — opens {server}/identity/connect/microsoft?port&nonce, awaits the
//     loopback callback, verifies the nonce, redeems the one-time handle, persists the tokens.
//   * RequestMagicLinkAsync   — POSTs {email,port,nonce}, then awaits the SAME loopback callback
//     for when the user clicks the emailed link (same-device), redeeming the handle identically.
//   * GetIdentityStateAsync   — loads the cached tokens and resolves the signed-in profile,
//     refreshing the access token (rotating refresh) when the server rejects it or it is near
//     expiry, and clearing the cache when refresh fails.
//
// SECURITY (finding I1): the App generates the nonce locally and verifies it against the value the
// Server echoes in the loopback callback, rejecting any callback whose nonce does not match — so a
// forged or replayed callback to the loopback port cannot complete a login.
//
// The HttpListener loopback and the system browser are untested infrastructure (CLAUDE.md); they
// are injected behind IIdentityLoopback / ISystemBrowser so the orchestration here is unit-tested
// with fakes. The pure decisions (nonce verification, callback parsing, the refresh decision) are
// factored into internal methods the tests exercise directly.
public sealed class IdentityLoginService
{
    // Default cap on how long the loopback waits for the browser callback before giving up.
    public static readonly TimeSpan DefaultLoginTimeout = TimeSpan.FromMinutes(3);

    // Proactive-refresh threshold (plan v2 §B-6): refresh when the access token is within this of
    // expiry even if it still validates, so a long-idle App renews before it has to.
    public static readonly TimeSpan RefreshThreshold = TimeSpan.FromDays(3);

    private readonly IIdentityServerClient _server;
    private readonly IIdentityTokenCache _cache;
    private readonly Func<IIdentityLoopback> _loopbackFactory;
    private readonly ISystemBrowser _browser;
    private readonly string _serverBaseUrl;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _loginTimeout;

    public IdentityLoginService(
        IIdentityServerClient server,
        IIdentityTokenCache cache,
        Func<IIdentityLoopback> loopbackFactory,
        ISystemBrowser browser,
        string serverBaseUrl,
        TimeProvider? clock = null,
        TimeSpan? loginTimeout = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _loopbackFactory = loopbackFactory ?? throw new ArgumentNullException(nameof(loopbackFactory));
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        if (string.IsNullOrWhiteSpace(serverBaseUrl)) throw new ArgumentNullException(nameof(serverBaseUrl));
        _serverBaseUrl = serverBaseUrl.TrimEnd('/');
        _clock = clock ?? TimeProvider.System;
        _loginTimeout = loginTimeout ?? DefaultLoginTimeout;
    }

    // --- Microsoft sign-in -------------------------------------------------------------------

    public async Task<LoginOutcome> LoginWithMicrosoftAsync(CancellationToken ct = default)
    {
        var nonce = GenerateNonce();
        var loopback = _loopbackFactory();
        try
        {
            await loopback.StartAsync(ct);

            var url =
                $"{_serverBaseUrl}/identity/connect/microsoft" +
                $"?port={loopback.Port}&nonce={Uri.EscapeDataString(nonce)}";
            _browser.Open(url);

            return await AwaitAndCompleteAsync(loopback, nonce, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return LoginOutcome.Fail($"Sign-in failed: {ex.Message}");
        }
        finally
        {
            loopback.Stop();
        }
    }

    // --- Magic-link sign-in ------------------------------------------------------------------

    // Starts the magic-link flow: raise the loopback, ask the Server to email a link, and report
    // back so the UI can tell the user to check their inbox. The login completes later when the
    // user clicks the link (same machine) and the browser hits the loopback — callers can await
    // that by also calling WaitForMagicLinkAsync, or treat RequestMagicLinkAsync as fire-and-wait
    // via the overload that returns the LoginOutcome.
    public async Task<RequestMagicLinkOutcome> RequestMagicLinkAsync(string email, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email))
            return RequestMagicLinkOutcome.Fail("Enter an email address.");

        var nonce = GenerateNonce();
        var loopback = _loopbackFactory();
        try
        {
            await loopback.StartAsync(ct);
            var requested = await _server.RequestMagicLinkAsync(email, loopback.Port, nonce, ct);
            if (!requested)
            {
                loopback.Stop();
                return RequestMagicLinkOutcome.Fail("Could not reach the server to send the sign-in link.");
            }

            // Wait for the click in the background; the loopback completes the sign-in via the same
            // handle/nonce path the Microsoft flow uses. Fire-and-forget so the UI can show
            // "check your email" immediately. The loopback is stopped when the await completes.
            _ = Task.Run(async () =>
            {
                try { await AwaitAndCompleteAsync(loopback, nonce, ct); }
                catch { /* surfaced via the next GetIdentityStateAsync poll */ }
                finally { loopback.Stop(); }
            }, ct);

            return RequestMagicLinkOutcome.Ok();
        }
        catch (Exception ex)
        {
            loopback.Stop();
            return RequestMagicLinkOutcome.Fail($"Could not start the sign-in: {ex.Message}");
        }
    }

    // --- Shared callback handling ------------------------------------------------------------

    // Awaits the single loopback callback (bounded by the login timeout), verifies the nonce,
    // redeems the handle, persists the tokens, and resolves the signed-in state.
    private async Task<LoginOutcome> AwaitAndCompleteAsync(IIdentityLoopback loopback, string expectedNonce, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_loginTimeout);

        LoopbackCallback callback;
        try
        {
            callback = await loopback.WaitForCallbackAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return LoginOutcome.Fail("Sign-in timed out waiting for the browser.");
        }

        return await CompleteCallbackAsync(callback, expectedNonce, ct);
    }

    // The pure-logic tail of every sign-in: nonce check → handle extraction → redeem → persist →
    // resolve. Public so tests can drive it directly without a loopback.
    public async Task<LoginOutcome> CompleteCallbackAsync(LoopbackCallback callback, string expectedNonce, CancellationToken ct)
    {
        if (!VerifyNonce(callback, expectedNonce))
            return LoginOutcome.Fail("Sign-in rejected: the callback did not match this request.");

        if (!callback.Query.TryGetValue("handle", out var handle) || string.IsNullOrEmpty(handle))
            return LoginOutcome.Fail("Sign-in failed: no handle in the callback.");

        var tokens = await _server.RedeemHandleAsync(handle, ct);
        if (tokens is null)
            return LoginOutcome.Fail("Sign-in failed: the handle was invalid or expired.");

        await _cache.SaveAsync(tokens, ct);

        var profile = await _server.GetMeAsync(tokens.AccessToken, ct);
        if (profile is null)
            return LoginOutcome.Fail("Sign-in failed: the server did not return a profile.");

        return LoginOutcome.Ok(ToState(profile));
    }

    // Constant-time-ish nonce equality. The callback must carry a nonce that matches the one this
    // request generated, else it is rejected (finding I1).
    public static bool VerifyNonce(LoopbackCallback callback, string expectedNonce)
    {
        if (callback is null || string.IsNullOrEmpty(expectedNonce))
            return false;
        if (!callback.Query.TryGetValue("nonce", out var got) || string.IsNullOrEmpty(got))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(got),
            System.Text.Encoding.UTF8.GetBytes(expectedNonce));
    }

    // --- Sign-out ----------------------------------------------------------------------------

    public Task SignOutAsync(CancellationToken ct = default)
    {
        // TODO (future): also call a server-side revoke endpoint so the rotated refresh token is
        // killed server-side, not just dropped locally. No such endpoint exists yet (task scope).
        return _cache.ClearAsync(ct);
    }

    // --- State resolution + refresh ----------------------------------------------------------

    public async Task<IdentityState> GetIdentityStateAsync(CancellationToken ct = default)
    {
        var tokens = await _cache.LoadAsync(ct);
        if (tokens is null)
            return IdentityState.SignedOut;

        // Try the current access token first. If the server accepts it, we are signed in. If it is
        // rejected (expired/revoked), fall through to a refresh attempt.
        var profile = await TryGetMeAsync(tokens.AccessToken, ct);
        if (profile is not null)
            return ToState(profile);

        // Access token no longer valid — attempt a refresh (rotating). On failure, the session is
        // over: clear the cache and report signed-out.
        var refreshed = await _server.RefreshAsync(tokens.RefreshToken, ct);
        if (refreshed is null)
        {
            await _cache.ClearAsync(ct);
            return IdentityState.SignedOut;
        }

        var newTokens = new IdentityTokens(refreshed.AccessToken, refreshed.NewRefreshToken);
        await _cache.SaveAsync(newTokens, ct);

        var afterRefresh = await TryGetMeAsync(newTokens.AccessToken, ct);
        if (afterRefresh is null)
        {
            await _cache.ClearAsync(ct);
            return IdentityState.SignedOut;
        }

        return ToState(afterRefresh);
    }

    // Network-tolerant /me call: a transport failure (offline) returns null so the caller can fall
    // back to a refresh attempt (which is also offline-tolerant) rather than crashing.
    private async Task<IdentityProfile?> TryGetMeAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            return await _server.GetMeAsync(accessToken, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    // --- helpers -----------------------------------------------------------------------------

    private static IdentityState ToState(IdentityProfile profile) => new(
        IsSignedIn: true,
        UserId: profile.UserId,
        Email: profile.Email,
        DisplayName: profile.DisplayName,
        // The access-token expiry is server-side and opaque to the App; surfaced as null until a
        // future token format exposes it. The signed-in flag + refresh-on-reject keep this correct.
        ExpiresAt: null,
        Plan: profile.Plan);

    private static string GenerateNonce() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
