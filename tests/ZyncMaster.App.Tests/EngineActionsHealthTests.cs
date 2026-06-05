using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using ZyncMaster.App.Bridge;
using ZyncMaster.App.Configuration;
using ZyncMaster.Core;
using ZyncMaster.Engine;
using Xunit;

namespace ZyncMaster.App.Tests;

// Covers EngineActions.CheckServerHealthAsync only — the warm-up / health probe used at App boot.
// The HttpClient is driven by a stub handler so we exercise ok / non-2xx / timeout / transport-
// failure without a live server. Every other EngineActions dependency is a bare mock: the probe
// touches none of them.
public class EngineActionsHealthTests
{
    // A pluggable HttpMessageHandler: each request runs the supplied delegate.
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;
        public Uri? LastUri { get; private set; }

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
            => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            return _handler(request, ct);
        }
    }

    private static EngineActions Build(HttpClient http, string serverBaseUrl)
    {
        var settings = new EngineSettings { ServerBaseUrl = serverBaseUrl };

        var keys = new Mock<IDeviceKeyStore>().Object;
        var pairing = new PairingService(
            new Mock<IPairingClient>().Object,
            new Mock<IBrowserLauncher>().Object,
            keys,
            settings);
        var sync = new SyncEngine(
            keys,
            new Mock<ICalendarSource>().Object,
            new Mock<ISyncClient>().Object,
            new Mock<IClock>().Object,
            settings);
        var identity = new IdentityLoginService(
            new Mock<IIdentityServerClient>().Object,
            new Mock<IIdentityTokenCache>().Object,
            () => new Mock<IIdentityLoopback>().Object,
            new Mock<ISystemBrowser>().Object,
            // IdentityLoginService rejects a blank base url; a placeholder is fine for these tests.
            string.IsNullOrWhiteSpace(serverBaseUrl) ? "https://placeholder.invalid" : serverBaseUrl);
        var calendarConnect = new CalendarConnectService(
            new Mock<ICalendarServerClient>().Object,
            new Mock<IIdentityTokenCache>().Object,
            () => new Mock<IIdentityLoopback>().Object,
            new Mock<ISystemBrowser>().Object);

        return new EngineActions(
            keys,
            pairing,
            sync,
            new Mock<ISettingsRepository<AppSettings>>().Object,
            new AppSettingsResolver(),
            "settings.json",
            new Mock<IPairsClient>().Object,
            new Mock<IIdentityTokenCache>().Object,
            new BasicTxtExporter(new Mock<ICalExportRunner>().Object),
            new Mock<IAutoStartManager>().Object,
            settings,
            _ => Task.FromResult<string?>(null),
            "host.exe",
            identity,
            calendarConnect,
            new Mock<IOutlookComProbe>().Object,
            new Mock<ICalendarSource>().Object,
            new Mock<IClock>().Object,
            http);
    }

    [Fact]
    public async Task Returns_ok_when_server_answers_2xx()
    {
        var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        using var http = new HttpClient(handler);
        var actions = Build(http, "https://server.test");

        var result = await actions.CheckServerHealthAsync();

        result.Ok.Should().BeTrue();
        result.Status.Should().Be("ok");
        handler.LastUri!.ToString().Should().Be("https://server.test/health");
    }

    [Fact]
    public async Task Returns_waking_when_server_answers_5xx()
    {
        var handler = new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var http = new HttpClient(handler);
        var actions = Build(http, "https://server.test/");

        var result = await actions.CheckServerHealthAsync();

        result.Ok.Should().BeFalse();
        result.Status.Should().Be("waking");
    }

    [Fact]
    public async Task Returns_waking_when_probe_times_out()
    {
        // The handler honours the per-attempt timeout by waiting on the token and throwing on cancel,
        // mimicking a cold-starting server that never answers within the budget.
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var http = new HttpClient(handler);
        var actions = Build(http, "https://server.test");

        var result = await actions.CheckServerHealthAsync();

        result.Ok.Should().BeFalse();
        result.Status.Should().Be("waking");
    }

    [Fact]
    public async Task Returns_unreachable_on_transport_failure()
    {
        var handler = new StubHandler((_, _) =>
            throw new HttpRequestException("No such host is known."));
        using var http = new HttpClient(handler);
        var actions = Build(http, "https://server.test");

        var result = await actions.CheckServerHealthAsync();

        result.Ok.Should().BeFalse();
        result.Status.Should().Be("unreachable");
    }

    [Fact]
    public async Task Returns_unconfigured_when_no_server_url()
    {
        using var http = new HttpClient(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));
        var actions = Build(http, "");

        var result = await actions.CheckServerHealthAsync();

        result.Ok.Should().BeFalse();
        result.Status.Should().Be("unconfigured");
    }

    [Fact]
    public async Task Propagates_caller_cancellation()
    {
        var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var http = new HttpClient(handler);
        var actions = Build(http, "https://server.test");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await actions.CheckServerHealthAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
