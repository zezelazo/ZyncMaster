using System;
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

// Auto-registration of the device after sign-in: EnsureDeviceRegisteredAsync (idempotent, signed-in
// gated, best-effort) and the RequireKeyAsync self-heal that registers on first use. Every boundary
// is a mock: the pairs client (RegisterDeviceAsync), the identity cache (signed-in bearer), and the
// device key store. No server, no network.
public class EngineActionsDeviceRegistrationTests
{
    // A device key store that starts empty and remembers the last SaveAsync, so a register-then-load
    // round-trip behaves like the real DPAPI store.
    private sealed class InMemoryKeyStore : IDeviceKeyStore
    {
        private string? _key;
        public int SaveCalls;
        public InMemoryKeyStore(string? initial = null) => _key = initial;
        public Task SaveAsync(string apiKey, CancellationToken ct) { _key = apiKey; SaveCalls++; return Task.CompletedTask; }
        public Task<string?> LoadAsync(CancellationToken ct) => Task.FromResult(_key);
        public Task ClearAsync(CancellationToken ct) { _key = null; return Task.CompletedTask; }
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

    private static EngineActions Build(
        IDeviceKeyStore keys,
        Mock<IPairsClient> pairs,
        Mock<IIdentityTokenCache> identityCache,
        EngineSettings? settings = null)
    {
        settings ??= new EngineSettings { ServerBaseUrl = "https://server.test", DeviceName = "Test-Device" };

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
            "https://server.test");
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
            pairs.Object,
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
            ZyncMaster.Core.NullAppLogger.Instance);
    }

    // ---------------- EnsureDeviceRegisteredAsync ----------------

    [Fact]
    public async Task Ensure_registers_and_saves_key_when_signed_in_and_no_key()
    {
        var keys = new InMemoryKeyStore();
        var pairs = new Mock<IPairsClient>();
        pairs.Setup(p => p.RegisterDeviceAsync("bearer-1", "Test-Device", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceRegistration { DeviceId = "dev-1", ApiKey = "kid.secret", LeaseUntil = DateTimeOffset.UtcNow });

        var actions = Build(keys, pairs, SignedIn());

        var key = await actions.EnsureDeviceRegisteredAsync(CancellationToken.None);

        key.Should().Be("kid.secret");
        (await keys.LoadAsync(CancellationToken.None)).Should().Be("kid.secret");
        keys.SaveCalls.Should().Be(1);
        pairs.Verify(p => p.RegisterDeviceAsync("bearer-1", "Test-Device", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Ensure_uses_machine_name_when_device_name_blank()
    {
        var keys = new InMemoryKeyStore();
        var pairs = new Mock<IPairsClient>();
        pairs.Setup(p => p.RegisterDeviceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceRegistration { DeviceId = "dev-1", ApiKey = "kid.secret" });

        var settings = new EngineSettings { ServerBaseUrl = "https://server.test", DeviceName = "   " };
        var actions = Build(keys, pairs, SignedIn(), settings);

        await actions.EnsureDeviceRegisteredAsync(CancellationToken.None);

        pairs.Verify(p => p.RegisterDeviceAsync("bearer-1", Environment.MachineName, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Ensure_is_noop_when_key_already_present()
    {
        var keys = new InMemoryKeyStore("existing-key");
        var pairs = new Mock<IPairsClient>();

        var actions = Build(keys, pairs, SignedIn());

        var key = await actions.EnsureDeviceRegisteredAsync(CancellationToken.None);

        key.Should().Be("existing-key");
        keys.SaveCalls.Should().Be(0);
        pairs.Verify(p => p.RegisterDeviceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Ensure_is_noop_when_not_signed_in()
    {
        var keys = new InMemoryKeyStore();
        var pairs = new Mock<IPairsClient>();

        var actions = Build(keys, pairs, SignedOut());

        var key = await actions.EnsureDeviceRegisteredAsync(CancellationToken.None);

        key.Should().BeNull();
        keys.SaveCalls.Should().Be(0);
        pairs.Verify(p => p.RegisterDeviceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Ensure_swallows_register_failure_and_returns_null()
    {
        var keys = new InMemoryKeyStore();
        var pairs = new Mock<IPairsClient>();
        pairs.Setup(p => p.RegisterDeviceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SyncClientException("boom 401"));

        var actions = Build(keys, pairs, SignedIn());

        var key = await actions.EnsureDeviceRegisteredAsync(CancellationToken.None);

        // A failed registration must NOT throw (boot/post-login resilience) and must NOT persist a key.
        key.Should().BeNull();
        keys.SaveCalls.Should().Be(0);
    }

    [Fact]
    public async Task Ensure_does_not_save_when_server_returns_empty_key()
    {
        var keys = new InMemoryKeyStore();
        var pairs = new Mock<IPairsClient>();
        pairs.Setup(p => p.RegisterDeviceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceRegistration { DeviceId = "dev-1", ApiKey = "" });

        var actions = Build(keys, pairs, SignedIn());

        var key = await actions.EnsureDeviceRegisteredAsync(CancellationToken.None);

        key.Should().BeNull();
        keys.SaveCalls.Should().Be(0);
    }

    // ---------------- RequireKeyAsync self-heal (exercised via GetDeviceAsync) ----------------

    [Fact]
    public async Task GetDevice_self_heals_by_registering_when_signed_in_but_no_key()
    {
        var keys = new InMemoryKeyStore();
        var pairs = new Mock<IPairsClient>();
        pairs.Setup(p => p.RegisterDeviceAsync("bearer-1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceRegistration { DeviceId = "dev-1", ApiKey = "healed-key" });
        pairs.Setup(p => p.GetDeviceMeAsync("healed-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceInfo { DeviceId = "dev-1", Name = "Test-Device", Platform = "windows" });

        var actions = Build(keys, pairs, SignedIn());

        var info = await actions.GetDeviceAsync(CancellationToken.None);

        info.DeviceId.Should().Be("dev-1");
        // The self-heal registered AND the subsequent device read used the freshly minted key.
        pairs.Verify(p => p.RegisterDeviceAsync("bearer-1", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        pairs.Verify(p => p.GetDeviceMeAsync("healed-key", It.IsAny<CancellationToken>()), Times.Once);
        (await keys.LoadAsync(CancellationToken.None)).Should().Be("healed-key");
    }

    [Fact]
    public async Task GetDevice_uses_existing_key_without_registering()
    {
        var keys = new InMemoryKeyStore("device-key");
        var pairs = new Mock<IPairsClient>();
        pairs.Setup(p => p.GetDeviceMeAsync("device-key", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeviceInfo { DeviceId = "dev-1", Name = "X", Platform = "windows" });

        var actions = Build(keys, pairs, SignedIn());

        await actions.GetDeviceAsync(CancellationToken.None);

        pairs.Verify(p => p.RegisterDeviceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        pairs.Verify(p => p.GetDeviceMeAsync("device-key", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetDevice_throws_signin_message_when_no_key_and_not_signed_in()
    {
        var keys = new InMemoryKeyStore();
        var pairs = new Mock<IPairsClient>();

        var actions = Build(keys, pairs, SignedOut());

        Func<Task> act = () => actions.GetDeviceAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("Sign in");
        pairs.Verify(p => p.RegisterDeviceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetDevice_throws_when_signed_in_but_registration_unavailable()
    {
        var keys = new InMemoryKeyStore();
        var pairs = new Mock<IPairsClient>();
        // Registration fails transiently (network/server), so no key is obtained.
        pairs.Setup(p => p.RegisterDeviceAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SyncClientException("server down"));

        var actions = Build(keys, pairs, SignedIn());

        Func<Task> act = () => actions.GetDeviceAsync(CancellationToken.None);

        // Signed in, so the message is the "could not register yet" variant, NOT "sign in".
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("could not be registered");
    }
}
