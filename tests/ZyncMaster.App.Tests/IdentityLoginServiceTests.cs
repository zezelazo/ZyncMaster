using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ZyncMaster.App.Bridge;
using Xunit;

namespace ZyncMaster.App.Tests;

// Tests the pure, testable logic of IdentityLoginService: nonce verification (finding I1),
// callback parsing/handle-error handling, the timeout path, and the refresh decision in
// GetIdentityStateAsync. The HttpListener loopback and the system browser are untested
// infrastructure (CLAUDE.md) — substituted here by fakes of IIdentityLoopback / ISystemBrowser.
public class IdentityLoginServiceTests
{
    private const string BaseUrl = "https://srv.example.com";

    // ---- fakes --------------------------------------------------------------------------------

    private sealed class FakeServer : IIdentityServerClient
    {
        public IdentityTokens? RedeemResult;
        public RefreshResult? RefreshResultValue;
        public IdentityProfile? MeResult;
        public IdentityProfile? MeAfterRefresh; // returned on the SECOND GetMe call when set
        public bool MagicLinkOk = true;
        public Exception? MeThrows;

        public int RedeemCalls, RefreshCalls, GetMeCalls, MagicLinkCalls;
        public string? LastHandle, LastRefreshToken, LastMagicEmail, LastMagicNonce;
        public int LastMagicPort;

        public Task<IdentityTokens?> RedeemHandleAsync(string handle, CancellationToken ct = default)
        {
            RedeemCalls++;
            LastHandle = handle;
            return Task.FromResult(RedeemResult);
        }

        public Task<RefreshResult?> RefreshAsync(string refreshToken, CancellationToken ct = default)
        {
            RefreshCalls++;
            LastRefreshToken = refreshToken;
            return Task.FromResult(RefreshResultValue);
        }

        public Task<IdentityProfile?> GetMeAsync(string accessToken, CancellationToken ct = default)
        {
            GetMeCalls++;
            if (MeThrows != null) throw MeThrows;
            // First call returns MeResult; once a refresh has happened, MeAfterRefresh (if set).
            if (GetMeCalls > 1 && MeAfterRefresh != null)
                return Task.FromResult<IdentityProfile?>(MeAfterRefresh);
            return Task.FromResult(MeResult);
        }

        public Task<bool> RequestMagicLinkAsync(string email, int port, string nonce, CancellationToken ct = default)
        {
            MagicLinkCalls++;
            LastMagicEmail = email;
            LastMagicPort = port;
            LastMagicNonce = nonce;
            return Task.FromResult(MagicLinkOk);
        }
    }

    private sealed class FakeCache : IIdentityTokenCache
    {
        public IdentityTokens? Stored;
        public int SaveCalls, ClearCalls;

        public Task SaveAsync(IdentityTokens tokens, CancellationToken ct = default)
        {
            SaveCalls++;
            Stored = tokens;
            return Task.CompletedTask;
        }

        public Task<IdentityTokens?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Stored);

        public Task ClearAsync(CancellationToken ct = default)
        {
            ClearCalls++;
            Stored = null;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeBrowser : ISystemBrowser
    {
        public string? LastUrl;
        public int OpenCalls;
        // Lets a test react to the (internally-generated) URL — e.g. relay the nonce into the
        // loopback callback so verification passes deterministically.
        public Action<string>? OpenCallsRelay;
        public void Open(string url)
        {
            OpenCalls++;
            LastUrl = url;
            OpenCallsRelay?.Invoke(url);
        }
    }

    // A loopback whose callback / timing the test controls.
    private sealed class FakeLoopback : IIdentityLoopback
    {
        public int Port { get; set; } = 50505;
        public LoopbackCallback? CallbackToReturn;
        public bool TimeOut;       // when true, WaitForCallbackAsync honours the token (never returns)
        public int StartCalls, StopCalls, WaitCalls;

        public Task StartAsync(CancellationToken ct = default) { StartCalls++; return Task.CompletedTask; }

        public async Task<LoopbackCallback> WaitForCallbackAsync(CancellationToken ct = default)
        {
            WaitCalls++;
            if (TimeOut)
            {
                // Block until the linked timeout cancels, then surface cancellation like the real one.
                await Task.Delay(Timeout.Infinite, ct);
            }
            return CallbackToReturn ?? new LoopbackCallback(new Dictionary<string, string>());
        }

        public void Stop() => StopCalls++;
    }

    private static LoopbackCallback Callback(params (string k, string v)[] pairs)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in pairs) d[k] = v;
        return new LoopbackCallback(d);
    }

    private static IdentityLoginService Build(
        FakeServer server, FakeCache cache, FakeLoopback loopback, FakeBrowser browser,
        TimeSpan? timeout = null)
        => new(server, cache, () => loopback, browser, BaseUrl, TimeProvider.System,
               timeout ?? TimeSpan.FromMilliseconds(200));

    // ---- ctor guards --------------------------------------------------------------------------

    [Fact]
    public void Ctor_null_args_throw()
    {
        var s = new FakeServer(); var c = new FakeCache(); var l = new FakeLoopback(); var b = new FakeBrowser();
        ((Action)(() => new IdentityLoginService(null!, c, () => l, b, BaseUrl))).Should().Throw<ArgumentNullException>();
        ((Action)(() => new IdentityLoginService(s, null!, () => l, b, BaseUrl))).Should().Throw<ArgumentNullException>();
        ((Action)(() => new IdentityLoginService(s, c, null!, b, BaseUrl))).Should().Throw<ArgumentNullException>();
        ((Action)(() => new IdentityLoginService(s, c, () => l, null!, BaseUrl))).Should().Throw<ArgumentNullException>();
        ((Action)(() => new IdentityLoginService(s, c, () => l, b, ""))).Should().Throw<ArgumentNullException>();
    }

    // ---- nonce verification (finding I1) ------------------------------------------------------

    [Fact]
    public void VerifyNonce_matches_when_equal()
    {
        IdentityLoginService.VerifyNonce(Callback(("nonce", "abc")), "abc").Should().BeTrue();
    }

    [Fact]
    public void VerifyNonce_rejects_mismatch_missing_or_empty()
    {
        IdentityLoginService.VerifyNonce(Callback(("nonce", "abc")), "xyz").Should().BeFalse();
        IdentityLoginService.VerifyNonce(Callback(("handle", "h")), "abc").Should().BeFalse();
        IdentityLoginService.VerifyNonce(Callback(("nonce", "")), "abc").Should().BeFalse();
        IdentityLoginService.VerifyNonce(Callback(("nonce", "abc")), "").Should().BeFalse();
    }

    // ---- CompleteCallbackAsync (the shared tail) ----------------------------------------------

    [Fact]
    public async Task CompleteCallback_happy_path_redeems_persists_and_returns_state()
    {
        var server = new FakeServer
        {
            RedeemResult = new IdentityTokens("access-1", "refresh-1"),
            MeResult = new IdentityProfile("u1", "u1@test", "User One", "free"),
        };
        var cache = new FakeCache();
        var svc = Build(server, cache, new FakeLoopback(), new FakeBrowser());

        var outcome = await svc.CompleteCallbackAsync(Callback(("nonce", "N"), ("handle", "H")), "N", default);

        outcome.Success.Should().BeTrue();
        outcome.State!.IsSignedIn.Should().BeTrue();
        outcome.State.UserId.Should().Be("u1");
        outcome.State.Email.Should().Be("u1@test");
        outcome.State.Plan.Should().Be("free");
        server.LastHandle.Should().Be("H");
        cache.Stored.Should().Be(new IdentityTokens("access-1", "refresh-1"));
    }

    [Fact]
    public async Task CompleteCallback_rejects_nonce_mismatch_without_redeeming()
    {
        var server = new FakeServer { RedeemResult = new IdentityTokens("a", "r") };
        var cache = new FakeCache();
        var svc = Build(server, cache, new FakeLoopback(), new FakeBrowser());

        var outcome = await svc.CompleteCallbackAsync(Callback(("nonce", "WRONG"), ("handle", "H")), "EXPECTED", default);

        outcome.Success.Should().BeFalse();
        outcome.Error.Should().Contain("did not match");
        server.RedeemCalls.Should().Be(0);
        cache.SaveCalls.Should().Be(0);
    }

    [Fact]
    public async Task CompleteCallback_missing_handle_fails()
    {
        var server = new FakeServer();
        var svc = Build(server, new FakeCache(), new FakeLoopback(), new FakeBrowser());

        var outcome = await svc.CompleteCallbackAsync(Callback(("nonce", "N")), "N", default);

        outcome.Success.Should().BeFalse();
        outcome.Error.Should().Contain("no handle");
        server.RedeemCalls.Should().Be(0);
    }

    [Fact]
    public async Task CompleteCallback_handle_redeem_failure_fails_and_does_not_persist()
    {
        var server = new FakeServer { RedeemResult = null }; // 410 from server
        var cache = new FakeCache();
        var svc = Build(server, cache, new FakeLoopback(), new FakeBrowser());

        var outcome = await svc.CompleteCallbackAsync(Callback(("nonce", "N"), ("handle", "H")), "N", default);

        outcome.Success.Should().BeFalse();
        outcome.Error.Should().Contain("invalid or expired");
        cache.SaveCalls.Should().Be(0);
    }

    [Fact]
    public async Task CompleteCallback_me_returns_null_after_redeem_fails()
    {
        var server = new FakeServer
        {
            RedeemResult = new IdentityTokens("a", "r"),
            MeResult = null,
        };
        var svc = Build(server, new FakeCache(), new FakeLoopback(), new FakeBrowser());

        var outcome = await svc.CompleteCallbackAsync(Callback(("nonce", "N"), ("handle", "H")), "N", default);

        outcome.Success.Should().BeFalse();
        outcome.Error.Should().Contain("profile");
    }

    // ---- LoginWithMicrosoftAsync (orchestration over the fake loopback) -----------------------

    [Fact]
    public async Task LoginWithMicrosoft_opens_browser_with_port_and_nonce_then_completes()
    {
        var server = new FakeServer
        {
            RedeemResult = new IdentityTokens("a", "r"),
            MeResult = new IdentityProfile("u1", "u1@test", "User One", null),
        };
        var loopback = new FakeLoopback { Port = 49999 };
        var browser = new FakeBrowser();
        var svc = Build(server, new FakeCache(), loopback, browser);

        // The browser-open captures the nonce; feed the SAME nonce back via the callback so the
        // verification passes. The service generates the nonce internally, so derive it from the URL.
        // To make this deterministic we intercept: open is synchronous and sets LastUrl before the
        // wait, but the callback is returned by the fake. So set the callback's nonce to match after
        // capturing — emulate by pre-seeding the loopback callback from the browser URL via a relay.
        browser.OpenCallsRelay = url =>
        {
            var nonce = ExtractQuery(url, "nonce");
            loopback.CallbackToReturn = Callback(("nonce", nonce), ("handle", "H"));
        };

        var outcome = await svc.LoginWithMicrosoftAsync();

        outcome.Success.Should().BeTrue();
        browser.OpenCalls.Should().Be(1);
        browser.LastUrl.Should().StartWith($"{BaseUrl}/identity/connect/microsoft?port=49999&nonce=");
        loopback.StartCalls.Should().Be(1);
        loopback.StopCalls.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task LoginWithMicrosoft_times_out_when_callback_never_arrives()
    {
        var loopback = new FakeLoopback { TimeOut = true };
        var svc = Build(new FakeServer(), new FakeCache(), loopback, new FakeBrowser(),
                        timeout: TimeSpan.FromMilliseconds(50));

        var outcome = await svc.LoginWithMicrosoftAsync();

        outcome.Success.Should().BeFalse();
        outcome.Error.Should().Contain("timed out");
        loopback.StopCalls.Should().BeGreaterThanOrEqualTo(1);
    }

    // ---- RequestMagicLinkAsync ----------------------------------------------------------------

    [Fact]
    public async Task RequestMagicLink_posts_with_port_and_nonce_and_reports_requested()
    {
        var server = new FakeServer { MagicLinkOk = true };
        var loopback = new FakeLoopback { TimeOut = true }; // never clicked during the test
        var svc = Build(server, new FakeCache(), loopback, new FakeBrowser(),
                        timeout: TimeSpan.FromMilliseconds(50));

        var outcome = await svc.RequestMagicLinkAsync("me@test");

        outcome.Requested.Should().BeTrue();
        server.MagicLinkCalls.Should().Be(1);
        server.LastMagicEmail.Should().Be("me@test");
        server.LastMagicPort.Should().Be(loopback.Port);
        server.LastMagicNonce.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RequestMagicLink_empty_email_fails_fast()
    {
        var server = new FakeServer();
        var svc = Build(server, new FakeCache(), new FakeLoopback(), new FakeBrowser());

        var outcome = await svc.RequestMagicLinkAsync("   ");

        outcome.Requested.Should().BeFalse();
        server.MagicLinkCalls.Should().Be(0);
    }

    [Fact]
    public async Task RequestMagicLink_server_unreachable_reports_failure()
    {
        var server = new FakeServer { MagicLinkOk = false };
        var svc = Build(server, new FakeCache(), new FakeLoopback(), new FakeBrowser());

        var outcome = await svc.RequestMagicLinkAsync("me@test");

        outcome.Requested.Should().BeFalse();
        outcome.Error.Should().Contain("server");
    }

    // ---- SignOutAsync -------------------------------------------------------------------------

    [Fact]
    public async Task SignOut_clears_the_cache()
    {
        var cache = new FakeCache { Stored = new IdentityTokens("a", "r") };
        var svc = Build(new FakeServer(), cache, new FakeLoopback(), new FakeBrowser());

        await svc.SignOutAsync();

        cache.ClearCalls.Should().Be(1);
        cache.Stored.Should().BeNull();
    }

    // ---- GetIdentityStateAsync (the refresh decision) -----------------------------------------

    [Fact]
    public async Task GetState_no_cache_returns_signed_out()
    {
        var svc = Build(new FakeServer(), new FakeCache(), new FakeLoopback(), new FakeBrowser());

        var state = await svc.GetIdentityStateAsync();

        state.IsSignedIn.Should().BeFalse();
    }

    [Fact]
    public async Task GetState_valid_token_returns_signed_in_without_refresh()
    {
        var server = new FakeServer { MeResult = new IdentityProfile("u1", "u1@test", "User One", "pro") };
        var cache = new FakeCache { Stored = new IdentityTokens("good-access", "refresh") };
        var svc = Build(server, cache, new FakeLoopback(), new FakeBrowser());

        var state = await svc.GetIdentityStateAsync();

        state.IsSignedIn.Should().BeTrue();
        state.Plan.Should().Be("pro");
        server.RefreshCalls.Should().Be(0);
    }

    [Fact]
    public async Task GetState_expired_token_refreshes_persists_and_returns_signed_in()
    {
        var server = new FakeServer
        {
            MeResult = null, // first /me rejects the old access token
            RefreshResultValue = new RefreshResult("new-access", "new-refresh"),
            MeAfterRefresh = new IdentityProfile("u1", "u1@test", "User One", null), // second /me ok
        };
        var cache = new FakeCache { Stored = new IdentityTokens("old-access", "old-refresh") };
        var svc = Build(server, cache, new FakeLoopback(), new FakeBrowser());

        var state = await svc.GetIdentityStateAsync();

        state.IsSignedIn.Should().BeTrue();
        server.RefreshCalls.Should().Be(1);
        server.LastRefreshToken.Should().Be("old-refresh");
        cache.Stored.Should().Be(new IdentityTokens("new-access", "new-refresh"));
    }

    [Fact]
    public async Task GetState_refresh_failure_clears_cache_and_returns_signed_out()
    {
        var server = new FakeServer
        {
            MeResult = null,            // access token rejected
            RefreshResultValue = null,  // refresh also rejected (expired/revoked)
        };
        var cache = new FakeCache { Stored = new IdentityTokens("old-access", "old-refresh") };
        var svc = Build(server, cache, new FakeLoopback(), new FakeBrowser());

        var state = await svc.GetIdentityStateAsync();

        state.IsSignedIn.Should().BeFalse();
        server.RefreshCalls.Should().Be(1);
        cache.ClearCalls.Should().BeGreaterThanOrEqualTo(1);
        cache.Stored.Should().BeNull();
    }

    [Fact]
    public async Task GetState_me_throws_offline_falls_back_to_refresh()
    {
        var server = new FakeServer
        {
            MeThrows = new System.Net.Http.HttpRequestException("offline"),
            RefreshResultValue = null,
        };
        var cache = new FakeCache { Stored = new IdentityTokens("a", "r") };
        var svc = Build(server, cache, new FakeLoopback(), new FakeBrowser());

        var state = await svc.GetIdentityStateAsync();

        // /me threw → treated as rejected → refresh attempted → refresh null → signed out + cleared.
        state.IsSignedIn.Should().BeFalse();
        server.RefreshCalls.Should().Be(1);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static string ExtractQuery(string url, string key)
    {
        var idx = url.IndexOf('?');
        if (idx < 0) return "";
        var qs = url[(idx + 1)..];
        foreach (var part in qs.Split('&'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0] == key)
                return Uri.UnescapeDataString(kv[1]);
        }
        return "";
    }
}
