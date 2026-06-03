using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ZyncMaster.App.Bridge;
using Xunit;

namespace ZyncMaster.App.Tests;

// Tests the testable orchestration of CalendarConnectService: the not-signed-in guard, nonce
// verification (finding I1), the status check, the timeout path, cancel/supersede, and the
// account-list pass-through. The HttpListener loopback and the system browser are untested
// infrastructure (CLAUDE.md) — substituted here by fakes of IIdentityLoopback / ISystemBrowser.
public class CalendarConnectServiceTests
{
    // ---- fakes --------------------------------------------------------------------------------

    private sealed class FakeServer : ICalendarServerClient
    {
        public string? AuthorizeUrlToReturn = "https://login.microsoftonline.com/authorize?x=1";
        public IReadOnlyList<CalendarAccountSummary> AccountsToReturn = new List<CalendarAccountSummary>();

        public int StartCalls, ListCalls;
        public string? LastAccessToken, LastScope, LastNonce;
        public int LastPort;

        public Task<string?> StartGraphConnectAsync(
            string accessToken, string scope, int port, string nonce, CancellationToken ct = default)
        {
            StartCalls++;
            LastAccessToken = accessToken;
            LastScope = scope;
            LastPort = port;
            LastNonce = nonce;
            return Task.FromResult(AuthorizeUrlToReturn);
        }

        public Task<IReadOnlyList<CalendarAccountSummary>> ListCalendarAccountsAsync(
            string accessToken, CancellationToken ct = default)
        {
            ListCalls++;
            LastAccessToken = accessToken;
            return Task.FromResult(AccountsToReturn);
        }
    }

    private sealed class FakeCache : IIdentityTokenCache
    {
        public IdentityTokens? Stored;
        public Task SaveAsync(IdentityTokens tokens, CancellationToken ct = default) { Stored = tokens; return Task.CompletedTask; }
        public Task<IdentityTokens?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Stored);
        public Task ClearAsync(CancellationToken ct = default) { Stored = null; return Task.CompletedTask; }
    }

    private sealed class FakeBrowser : ISystemBrowser
    {
        public string? LastUrl;
        public int OpenCalls;
        public Action<string>? OpenCallsRelay;
        public void Open(string url) { OpenCalls++; LastUrl = url; OpenCallsRelay?.Invoke(url); }
    }

    private sealed class FakeLoopback : IIdentityLoopback
    {
        public int Port { get; set; } = 50607;
        public LoopbackCallback? CallbackToReturn;
        public bool TimeOut;
        public int StartCalls, StopCalls, WaitCalls;

        public Task StartAsync(CancellationToken ct = default) { StartCalls++; return Task.CompletedTask; }

        public async Task<LoopbackCallback> WaitForCallbackAsync(CancellationToken ct = default)
        {
            WaitCalls++;
            if (TimeOut)
                await Task.Delay(Timeout.Infinite, ct);
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

    private static CalendarConnectService Build(
        FakeServer server, FakeCache cache, FakeLoopback loopback, FakeBrowser browser, TimeSpan? timeout = null)
        => new(server, cache, () => loopback, browser, timeout ?? TimeSpan.FromMilliseconds(200));

    private static FakeCache SignedIn() => new() { Stored = new IdentityTokens("access-1", "refresh-1") };

    // ---- ctor guards --------------------------------------------------------------------------

    [Fact]
    public void Ctor_null_args_throw()
    {
        var s = new FakeServer(); var c = new FakeCache(); var l = new FakeLoopback(); var b = new FakeBrowser();
        ((Action)(() => new CalendarConnectService(null!, c, () => l, b))).Should().Throw<ArgumentNullException>();
        ((Action)(() => new CalendarConnectService(s, null!, () => l, b))).Should().Throw<ArgumentNullException>();
        ((Action)(() => new CalendarConnectService(s, c, null!, b))).Should().Throw<ArgumentNullException>();
        ((Action)(() => new CalendarConnectService(s, c, () => l, null!))).Should().Throw<ArgumentNullException>();
    }

    // ---- not-signed-in guard ------------------------------------------------------------------

    [Fact]
    public async Task Connect_without_identity_fails_not_signed_in_and_opens_no_browser()
    {
        var server = new FakeServer();
        var loopback = new FakeLoopback();
        var browser = new FakeBrowser();
        var svc = Build(server, new FakeCache(), loopback, browser); // empty cache => signed out

        var outcome = await svc.ConnectCalendarAsync("readwrite");

        outcome.Connected.Should().BeFalse();
        outcome.Error.Should().Contain("Sign in");
        browser.OpenCalls.Should().Be(0);
        server.StartCalls.Should().Be(0);
        loopback.StartCalls.Should().Be(0);
    }

    // ---- nonce verification (finding I1) ------------------------------------------------------

    [Fact]
    public void VerifyNonce_matches_when_equal()
        => CalendarConnectService.VerifyNonce(Callback(("nonce", "abc")), "abc").Should().BeTrue();

    [Fact]
    public void VerifyNonce_rejects_mismatch_missing_or_empty()
    {
        CalendarConnectService.VerifyNonce(Callback(("nonce", "abc")), "xyz").Should().BeFalse();
        CalendarConnectService.VerifyNonce(Callback(("status", "connected")), "abc").Should().BeFalse();
        CalendarConnectService.VerifyNonce(Callback(("nonce", "")), "abc").Should().BeFalse();
        CalendarConnectService.VerifyNonce(Callback(("nonce", "abc")), "").Should().BeFalse();
    }

    // ---- CompleteCallback (the shared tail) ---------------------------------------------------

    [Fact]
    public void CompleteCallback_status_connected_with_matching_nonce_succeeds()
    {
        var svc = Build(new FakeServer(), SignedIn(), new FakeLoopback(), new FakeBrowser());

        var outcome = svc.CompleteCallback(Callback(("nonce", "N"), ("status", "connected")), "N");

        outcome.Connected.Should().BeTrue();
        outcome.Error.Should().BeNull();
    }

    [Fact]
    public void CompleteCallback_nonce_mismatch_is_rejected()
    {
        var svc = Build(new FakeServer(), SignedIn(), new FakeLoopback(), new FakeBrowser());

        var outcome = svc.CompleteCallback(Callback(("nonce", "WRONG"), ("status", "connected")), "EXPECTED");

        outcome.Connected.Should().BeFalse();
        outcome.Error.Should().Contain("did not match");
    }

    [Fact]
    public void CompleteCallback_non_connected_status_fails()
    {
        var svc = Build(new FakeServer(), SignedIn(), new FakeLoopback(), new FakeBrowser());

        var outcome = svc.CompleteCallback(Callback(("nonce", "N"), ("status", "error")), "N");

        outcome.Connected.Should().BeFalse();
        outcome.Error.Should().Contain("did not complete");
    }

    // ---- ConnectCalendarAsync (orchestration over the fake loopback) --------------------------

    [Fact]
    public async Task Connect_happy_path_opens_authorize_url_and_completes_on_callback()
    {
        var server = new FakeServer { AuthorizeUrlToReturn = "https://authorize.example/x" };
        var loopback = new FakeLoopback { Port = 49321 };
        var browser = new FakeBrowser();
        var svc = Build(server, SignedIn(), loopback, browser);

        // Relay the nonce the service handed to the server into the loopback callback so the
        // verification passes deterministically (the service generates the nonce internally).
        browser.OpenCallsRelay = _ =>
            loopback.CallbackToReturn = Callback(("status", "connected"), ("nonce", server.LastNonce ?? ""));

        var outcome = await svc.ConnectCalendarAsync("readwrite");

        outcome.Connected.Should().BeTrue();
        server.StartCalls.Should().Be(1);
        server.LastAccessToken.Should().Be("access-1");
        server.LastScope.Should().Be("readwrite");
        server.LastPort.Should().Be(49321);
        browser.OpenCalls.Should().Be(1);
        browser.LastUrl.Should().Be("https://authorize.example/x");
        loopback.StartCalls.Should().Be(1);
        loopback.StopCalls.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Connect_nonce_mismatch_in_callback_does_not_connect()
    {
        var server = new FakeServer();
        var loopback = new FakeLoopback { Port = 49322 };
        var browser = new FakeBrowser();
        var svc = Build(server, SignedIn(), loopback, browser);

        // Feed back a DIFFERENT nonce than the one generated -> rejected.
        browser.OpenCallsRelay = _ =>
            loopback.CallbackToReturn = Callback(("status", "connected"), ("nonce", "not-the-real-nonce"));

        var outcome = await svc.ConnectCalendarAsync("readwrite");

        outcome.Connected.Should().BeFalse();
        outcome.Error.Should().Contain("did not match");
    }

    [Fact]
    public async Task Connect_server_returns_no_authorize_url_fails_before_browser()
    {
        var server = new FakeServer { AuthorizeUrlToReturn = null }; // server rejected (e.g. 401)
        var loopback = new FakeLoopback();
        var browser = new FakeBrowser();
        var svc = Build(server, SignedIn(), loopback, browser);

        var outcome = await svc.ConnectCalendarAsync("readwrite");

        outcome.Connected.Should().BeFalse();
        outcome.Error.Should().Contain("server");
        browser.OpenCalls.Should().Be(0);
        loopback.StopCalls.Should().BeGreaterThanOrEqualTo(1); // port released
    }

    [Fact]
    public async Task Connect_times_out_when_callback_never_arrives()
    {
        var loopback = new FakeLoopback { TimeOut = true };
        var svc = Build(new FakeServer(), SignedIn(), loopback, new FakeBrowser(),
                        timeout: TimeSpan.FromMilliseconds(50));

        var outcome = await svc.ConnectCalendarAsync("readwrite");

        outcome.Connected.Should().BeFalse();
        outcome.Error.Should().Contain("timed out");
        loopback.StopCalls.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Connect_caller_cancellation_propagates()
    {
        var loopback = new FakeLoopback { TimeOut = true };
        var svc = Build(new FakeServer(), SignedIn(), loopback, new FakeBrowser(),
                        timeout: TimeSpan.FromMinutes(3));

        using var cts = new CancellationTokenSource();
        var connect = svc.ConnectCalendarAsync("readwrite", cts.Token);
        await WaitUntil(() => loopback.WaitCalls >= 1);

        cts.Cancel();

        var act = async () => await connect;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- CancelConnect / single-attempt-in-flight ---------------------------------------------

    [Fact]
    public async Task CancelConnect_aborts_in_flight_connect_as_canceled_and_stops_loopback()
    {
        var loopback = new FakeLoopback { TimeOut = true };
        var svc = Build(new FakeServer(), SignedIn(), loopback, new FakeBrowser(),
                        timeout: TimeSpan.FromMinutes(3));

        var connect = svc.ConnectCalendarAsync("readwrite");
        await WaitUntil(() => loopback.WaitCalls >= 1);

        svc.CancelConnect();
        var outcome = await connect;

        outcome.Connected.Should().BeFalse();
        outcome.Cancelled.Should().BeTrue();
        outcome.Error.Should().BeNull();
        loopback.StopCalls.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void CancelConnect_with_nothing_in_flight_is_a_noop()
    {
        var svc = Build(new FakeServer(), SignedIn(), new FakeLoopback(), new FakeBrowser());
        ((Action)svc.CancelConnect).Should().NotThrow();
    }

    // ---- ListCalendarAccountsAsync ------------------------------------------------------------

    [Fact]
    public async Task ListCalendarAccounts_passes_bearer_and_returns_server_list()
    {
        var server = new FakeServer
        {
            AccountsToReturn = new List<CalendarAccountSummary>
            {
                new("acc-1", "Graph", "microsoft", "me@outlook.com", "ReadWrite", "active", "Personal"),
            },
        };
        var svc = Build(server, SignedIn(), new FakeLoopback(), new FakeBrowser());

        var list = await svc.ListCalendarAccountsAsync();

        list.Should().HaveCount(1);
        list[0].Id.Should().Be("acc-1");
        server.ListCalls.Should().Be(1);
        server.LastAccessToken.Should().Be("access-1");
    }

    [Fact]
    public async Task ListCalendarAccounts_without_identity_returns_empty_and_does_not_call_server()
    {
        var server = new FakeServer();
        var svc = Build(server, new FakeCache(), new FakeLoopback(), new FakeBrowser());

        var list = await svc.ListCalendarAccountsAsync();

        list.Should().BeEmpty();
        server.ListCalls.Should().Be(0);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static async Task WaitUntil(Func<bool> predicate, int timeoutMs = 2000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!predicate())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("Condition was not met in time.");
            await Task.Delay(10);
        }
    }
}
