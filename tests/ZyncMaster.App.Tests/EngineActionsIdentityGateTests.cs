using System;
using System.Collections.Generic;
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

// HasIdentityAsync is the network-free identity gate the clipboard boot uses to wait for sign-in
// instead of calling a device-key-gated action (GetDeviceAsync) before an identity exists — which
// is what produced the per-tick "no identity present" Warning storm (diagnosis §A / §1.2, fix #2).
//
// These tests pin the gate's contract: it reads the SAME on-disk identity cache the engine's
// registration path reads, reports signed-in vs signed-out correctly, never throws, and — crucially
// for the storm — logs NO Warning on the signed-out check (the old GetDeviceAsync path logged one
// Warning per attempt). Every boundary is mocked; no server, no network, no Outlook.
public class EngineActionsIdentityGateTests
{
    // Captures every Log call so a test can assert on the level/message mix (e.g. "no Warning was
    // logged while the user was signed out").
    private sealed class CapturingLogger : IAppLogger
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();
        public void Log(LogLevel level, string message, Exception? ex = null)
            => Entries.Add((level, message));
        public bool IsEnabled(LogLevel level) => true;
    }

    private static Mock<IIdentityTokenCache> SignedIn(string token = "bearer-1")
    {
        var cache = new Mock<IIdentityTokenCache>();
        cache.Setup(c => c.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdentityTokens(token, "refresh"));
        return cache;
    }

    private static Mock<IIdentityTokenCache> SignedOut()
    {
        var cache = new Mock<IIdentityTokenCache>();
        cache.Setup(c => c.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IdentityTokens?)null);
        return cache;
    }

    // An identity cache that holds an access token with no refresh token. The real
    // FileIdentityTokenCache returns null for such a blob, but a custom IIdentityTokenCache could
    // hand one back — HasIdentityAsync must still treat a present access token as "signed in".
    private static Mock<IIdentityTokenCache> AccessTokenOnly(string token = "bearer-only")
    {
        var cache = new Mock<IIdentityTokenCache>();
        cache.Setup(c => c.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdentityTokens(token, ""));
        return cache;
    }

    private static EngineActions Build(Mock<IIdentityTokenCache> identityCache, IAppLogger logger)
    {
        var settings = new EngineSettings { ServerBaseUrl = "https://server.test", DeviceName = "Test-Device" };
        var keys = new Mock<IDeviceKeyStore>();
        keys.Setup(k => k.LoadAsync(It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        var pairing = new PairingService(
            new Mock<IPairingClient>().Object,
            new Mock<IBrowserLauncher>().Object,
            keys.Object,
            settings);
        var sync = new SyncEngine(
            keys.Object,
            new Mock<ICalendarSource>().Object,
            new Mock<ISyncClient>().Object,
            new Mock<IClock>().Object,
            settings);
        var identity = new IdentityLoginService(
            new Mock<IIdentityServerClient>().Object,
            new Mock<IIdentityTokenCache>().Object,
            () => new Mock<IIdentityLoopback>().Object,
            new Mock<ISystemBrowser>().Object,
            "https://server.test");
        var calendarConnect = new CalendarConnectService(
            new Mock<ICalendarServerClient>().Object,
            new Mock<IIdentityTokenCache>().Object,
            () => new Mock<IIdentityLoopback>().Object,
            new Mock<ISystemBrowser>().Object);

        return new EngineActions(
            keys.Object,
            pairing,
            sync,
            new Mock<ISettingsRepository<AppSettings>>().Object,
            new AppSettingsResolver(),
            "settings.json",
            new Mock<IPairsClient>().Object,
            identityCache.Object,
            new BasicTxtExporter(new Mock<ICalExportRunner>().Object),
            new Mock<IAutoStartManager>().Object,
            settings,
            _ => Task.FromResult<string?>(null),
            "host.exe",
            identity,
            calendarConnect,
            new Mock<IOutlookComProbe>().Object,
            new Mock<ICalendarSource>().Object,
            new Mock<ICalExportRunner>().Object,
            new Mock<IClock>().Object,
            new HttpClient(),
            logger);
    }

    [Fact]
    public async Task HasIdentity_true_when_a_token_is_cached()
    {
        var cache = SignedIn();
        var actions = Build(cache, ZyncMaster.Core.NullAppLogger.Instance);

        (await actions.HasIdentityAsync(CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task HasIdentity_true_when_only_an_access_token_is_present()
    {
        // A usable identity for gating purposes is one with an access token; the refresh token is the
        // engine's concern, not the gate's.
        var actions = Build(AccessTokenOnly(), ZyncMaster.Core.NullAppLogger.Instance);

        (await actions.HasIdentityAsync(CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task HasIdentity_false_when_signed_out()
    {
        var actions = Build(SignedOut(), ZyncMaster.Core.NullAppLogger.Instance);

        (await actions.HasIdentityAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task HasIdentity_false_when_access_token_is_empty()
    {
        var cache = new Mock<IIdentityTokenCache>();
        cache.Setup(c => c.LoadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdentityTokens("", "refresh"));
        var actions = Build(cache, ZyncMaster.Core.NullAppLogger.Instance);

        (await actions.HasIdentityAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task HasIdentity_reads_the_same_store_the_engine_registers_against()
    {
        // The gate MUST consult the injected identity cache (the SAME FileIdentityTokenCache the
        // device-registration / RequireKey paths read), not some separate in-memory snapshot — that
        // shared source is what keeps the panel's "signed in" and the engine's "can register" in step.
        var cache = SignedIn();
        var actions = Build(cache, ZyncMaster.Core.NullAppLogger.Instance);

        await actions.HasIdentityAsync(CancellationToken.None);

        cache.Verify(c => c.LoadAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HasIdentity_does_not_log_a_warning_when_signed_out()
    {
        // The whole point of the gate: a signed-out boot must NOT emit the "no identity present"
        // Warning that GetDeviceAsync produced once per tick. The cheap presence check is silent.
        var logger = new CapturingLogger();
        var actions = Build(SignedOut(), logger);

        var result = await actions.HasIdentityAsync(CancellationToken.None);

        result.Should().BeFalse();
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task HasIdentity_returns_false_and_warns_once_when_the_store_read_throws()
    {
        // A corrupt/locked token file must not crash the boot gate: report "no identity" and log a
        // single Warning (diagnostic), NOT throw. One line, not a per-tick storm — the gate caller
        // only invokes this on its poll cadence.
        var logger = new CapturingLogger();
        var cache = new Mock<IIdentityTokenCache>();
        cache.Setup(c => c.LoadAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new System.IO.IOException("locked"));
        var actions = Build(cache, logger);

        var result = await actions.HasIdentityAsync(CancellationToken.None);

        result.Should().BeFalse();
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task HasIdentity_propagates_cancellation()
    {
        // Shutdown must surface as OperationCanceledException (so the clipboard boot's shutdown handler
        // catches it), not be swallowed into a misleading "false".
        var cache = new Mock<IIdentityTokenCache>();
        cache.Setup(c => c.LoadAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var actions = Build(cache, ZyncMaster.Core.NullAppLogger.Instance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => actions.HasIdentityAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
