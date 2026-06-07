using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace ZyncMaster.App.Bridge;

// Connects a Microsoft Graph calendar account into the signed-in user's pool in one click, reusing
// the identity already logged in. It mirrors IdentityLoginService's system-browser + loopback
// pattern exactly:
//
//   * loads the cached identity tokens to obtain the IdentityBearer access token (no identity ->
//     a clear "not signed in" outcome, NEVER a browser open);
//   * generates a nonce locally and raises an ephemeral 127.0.0.1 loopback on /calendar/callback;
//   * asks the Server (POST /api/calendar/connect/graph/start, bearer + scope + port + nonce) for
//     the Microsoft authorize URL and opens it in the system browser;
//   * awaits the single loopback callback the Server redirects to after persisting the account
//     (http://127.0.0.1:{port}/calendar/callback?status=connected&nonce=...);
//   * verifies the echoed nonce with a constant-time compare (finding I1) — a forged or replayed
//     callback cannot complete the connect — and reports Connected only on status=connected.
//
// The HttpListener loopback and the system browser are untested infrastructure (CLAUDE.md); they
// are injected behind IIdentityLoopback / ISystemBrowser so this orchestration is unit-tested with
// fakes. ICalendarServerClient and IIdentityTokenCache are likewise abstracted for the tests.
public sealed class CalendarConnectService
{
    // Default cap on how long the loopback waits for the browser callback before giving up.
    // Matches IdentityLoginService's 3-minute sign-in budget.
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromMinutes(3);

    private readonly ICalendarServerClient _server;
    private readonly IIdentityTokenCache _identityCache;
    private readonly Func<IIdentityLoopback> _loopbackFactory;
    private readonly ISystemBrowser _browser;
    private readonly TimeSpan _connectTimeout;

    // Single-attempt-in-flight bookkeeping, mirroring IdentityLoginService: only ONE connect may be
    // pending at a time so two concurrent connects never race for the loopback port. A fresh connect
    // supersedes a pending one by cancelling its CTS and stopping its loopback.
    private readonly object _attemptLock = new();
    private CancellationTokenSource? _attemptCts;
    private IIdentityLoopback? _attemptLoopback;

    public CalendarConnectService(
        ICalendarServerClient server,
        IIdentityTokenCache identityCache,
        Func<IIdentityLoopback> loopbackFactory,
        ISystemBrowser browser,
        TimeSpan? connectTimeout = null)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _identityCache = identityCache ?? throw new ArgumentNullException(nameof(identityCache));
        _loopbackFactory = loopbackFactory ?? throw new ArgumentNullException(nameof(loopbackFactory));
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _connectTimeout = connectTimeout ?? DefaultConnectTimeout;
    }

    // Connects a Graph calendar account at the requested scope ("read" | "readwrite"). The scope is
    // forwarded verbatim to the Server, which validates it and defaults blank to read/write.
    public Task<ConnectCalendarOutcome> ConnectCalendarAsync(string scope, CancellationToken ct = default)
        => RunInteractiveConnectAsync(
            (token, port, nonce, attemptToken) =>
                _server.StartGraphConnectAsync(token, scope ?? "readwrite", port, nonce, attemptToken),
            ct);

    // Upgrades an already-connected, read-only account to read/write by re-running the SAME
    // interactive browser+loopback consent flow as a fresh connect — only the Server call differs
    // (POST .../accounts/{id}/upgrade-scope instead of the fresh-connect start). On success the
    // Server has persisted the broader scope; the outcome mirrors ConnectCalendarAsync so the wizard
    // can treat a granted upgrade exactly like a connect.
    public Task<ConnectCalendarOutcome> UpgradeAccountScopeAsync(string accountId, CancellationToken ct = default)
        => RunInteractiveConnectAsync(
            (token, port, nonce, attemptToken) =>
                _server.UpgradeAccountScopeAsync(token, accountId, port, nonce, attemptToken),
            ct);

    // The shared trunk of the interactive connect/upgrade flow. It loads the identity bearer, raises
    // the loopback, asks the Server for the Microsoft authorize URL via the supplied startUrl
    // delegate (the ONLY thing that differs between connect and upgrade), opens the system browser,
    // and awaits + verifies the single nonce-checked callback. Single-attempt-in-flight bookkeeping
    // and cancel/timeout handling are identical for both callers.
    private async Task<ConnectCalendarOutcome> RunInteractiveConnectAsync(
        Func<string, int, string, CancellationToken, Task<string?>> startUrl, CancellationToken ct)
    {
        // Must be signed in: the connect is gated by the IdentityBearer and persisted under that
        // user. No identity -> a clear outcome, and crucially NO browser open.
        var tokens = await _identityCache.LoadAsync(ct);
        if (tokens is null || string.IsNullOrEmpty(tokens.AccessToken))
            return ConnectCalendarOutcome.Fail("Sign in before connecting a calendar account.");

        var nonce = GenerateNonce();
        var loopback = _loopbackFactory();
        var attemptCts = BeginAttempt(loopback, ct);
        try
        {
            await loopback.StartAsync(attemptCts.Token);

            var authorizeUrl = await startUrl(tokens.AccessToken, loopback.Port, nonce, attemptCts.Token);
            if (string.IsNullOrEmpty(authorizeUrl))
                return ConnectCalendarOutcome.Fail("Could not start the calendar connection on the server.");

            _browser.Open(authorizeUrl);

            return await AwaitAndCompleteAsync(loopback, nonce, attemptCts.Token, ct);
        }
        catch (OperationCanceledException) when (attemptCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Cancelled by the user (cancelConnect) or superseded by a newer connect — not an error.
            return ConnectCalendarOutcome.Canceled();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ConnectCalendarOutcome.Fail($"Connecting the calendar failed: {ex.Message}");
        }
        finally
        {
            EndAttempt(attemptCts, loopback);
        }
    }

    // Cancels any connect currently in flight and clears the slot. Cancelling the CTS aborts the
    // pending callback wait; Stop() releases the loopback port. Safe to call when nothing is pending.
    public void CancelConnect()
    {
        CancellationTokenSource? cts;
        IIdentityLoopback? loopback;
        lock (_attemptLock)
        {
            cts = _attemptCts;
            loopback = _attemptLoopback;
            _attemptCts = null;
            _attemptLoopback = null;
        }

        if (cts is not null)
        {
            try { cts.Cancel(); } catch { /* already disposed/cancelled */ }
        }
        try { loopback?.Stop(); } catch { /* already stopped */ }
    }

    // Awaits the single loopback callback (bounded by the connect timeout OR an explicit cancel),
    // verifies the nonce, and reports Connected only when status=connected. Mirrors
    // IdentityLoginService.AwaitAndCompleteAsync.
    private async Task<ConnectCalendarOutcome> AwaitAndCompleteAsync(
        IIdentityLoopback loopback, string expectedNonce, CancellationToken attemptToken, CancellationToken callerToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(attemptToken);
        timeoutCts.CancelAfter(_connectTimeout);

        LoopbackCallback callback;
        try
        {
            callback = await loopback.WaitForCallbackAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (attemptToken.IsCancellationRequested)
        {
            // Cancelled by the user / superseded by a newer connect — quiet, not an error.
            return ConnectCalendarOutcome.Canceled();
        }
        catch (OperationCanceledException)
        {
            // The linked CTS fired without the attempt token: the hard timeout elapsed.
            return ConnectCalendarOutcome.Fail("Connecting the calendar timed out waiting for the browser.");
        }

        return CompleteCallback(callback, expectedNonce);
    }

    // The pure-logic tail: nonce check (finding I1) -> status check. Public so tests can drive it
    // directly without a loopback, exactly like IdentityLoginService.CompleteCallbackAsync.
    public ConnectCalendarOutcome CompleteCallback(LoopbackCallback callback, string expectedNonce)
    {
        if (!VerifyNonce(callback, expectedNonce))
            return ConnectCalendarOutcome.Fail("Calendar connection rejected: the callback did not match this request.");

        var status = callback.Query.TryGetValue("status", out var s) ? s : "";
        if (!string.Equals(status, "connected", StringComparison.Ordinal))
            return ConnectCalendarOutcome.Fail("The calendar connection did not complete.");

        return ConnectCalendarOutcome.Ok();
    }

    // Constant-time nonce equality, identical to IdentityLoginService.VerifyNonce (finding I1): the
    // callback must carry the nonce this request generated, else it is rejected.
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

    // Lists the signed-in user's connected calendar accounts over the IdentityBearer. Returns an
    // empty list when the App is not signed in (no bearer to present).
    public async Task<IReadOnlyList<CalendarAccountSummary>> ListCalendarAccountsAsync(CancellationToken ct = default)
    {
        var tokens = await _identityCache.LoadAsync(ct);
        if (tokens is null || string.IsNullOrEmpty(tokens.AccessToken))
            return Array.Empty<CalendarAccountSummary>();

        return await _server.ListCalendarAccountsAsync(tokens.AccessToken, ct);
    }

    // --- Attempt lifecycle (single connect in flight) ----------------------------------------

    private CancellationTokenSource BeginAttempt(IIdentityLoopback loopback, CancellationToken ct)
    {
        CancelConnect(); // supersede any pending attempt cleanly before claiming the slot

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_attemptLock)
        {
            _attemptCts = cts;
            _attemptLoopback = loopback;
        }
        return cts;
    }

    private void EndAttempt(CancellationTokenSource cts, IIdentityLoopback loopback)
    {
        lock (_attemptLock)
        {
            if (ReferenceEquals(_attemptCts, cts))
            {
                _attemptCts = null;
                _attemptLoopback = null;
            }
        }
        try { loopback.Stop(); } catch { /* already stopped */ }
        cts.Dispose();
    }

    private static string GenerateNonce() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
