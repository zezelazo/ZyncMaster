using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SyncMaster.Engine;
using Xunit;

namespace SyncMaster.Engine.Tests;

public sealed class PairingServiceTests
{
    private sealed class FakePairingClient : IPairingClient
    {
        private readonly Queue<PairComplete> _completions;
        public int StartCallCount { get; private set; }
        public int CompleteCallCount { get; private set; }
        public string? LastDeviceName { get; private set; }
        public PairStart StartResult { get; set; } = new PairStart { PairingId = "p-1", Code = "WXYZ" };

        public FakePairingClient(params PairComplete[] completions)
        {
            _completions = new Queue<PairComplete>(completions);
        }

        public Task<PairStart> StartAsync(string deviceName, CancellationToken ct)
        {
            StartCallCount++;
            LastDeviceName = deviceName;
            return Task.FromResult(StartResult);
        }

        public Task<PairComplete> CompleteAsync(string pairingId, CancellationToken ct)
        {
            CompleteCallCount++;
            // Repeat the last queued completion once the queue is drained.
            var result = _completions.Count > 1 ? _completions.Dequeue() : _completions.Peek();
            return Task.FromResult(result);
        }
    }

    private sealed class FakeBrowserLauncher : IBrowserLauncher
    {
        public string? LastUrl { get; private set; }
        public int OpenCallCount { get; private set; }

        public void Open(string url)
        {
            OpenCallCount++;
            LastUrl = url;
        }
    }

    private sealed class FakeDeviceKeyStore : IDeviceKeyStore
    {
        public string? Stored { get; set; }
        public string? SavedKey { get; private set; }
        public int LoadCallCount { get; private set; }

        public Task SaveAsync(string apiKey, CancellationToken ct)
        {
            SavedKey = apiKey;
            Stored = apiKey;
            return Task.CompletedTask;
        }

        public Task<string?> LoadAsync(CancellationToken ct)
        {
            LoadCallCount++;
            return Task.FromResult(Stored);
        }

        public Task ClearAsync(CancellationToken ct)
        {
            Stored = null;
            return Task.CompletedTask;
        }
    }

    private static EngineSettings Settings() => new EngineSettings
    {
        ServerBaseUrl = "https://srv.example.com",
        DeviceName = "Test-Device",
    };

    [Fact]
    public void Ctor_NullPairing_Throws()
    {
        Action act = () => new PairingService(null!, new FakeBrowserLauncher(), new FakeDeviceKeyStore(), Settings());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullBrowser_Throws()
    {
        Action act = () => new PairingService(new FakePairingClient(), null!, new FakeDeviceKeyStore(), Settings());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullKeys_Throws()
    {
        Action act = () => new PairingService(new FakePairingClient(), new FakeBrowserLauncher(), null!, Settings());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullSettings_Throws()
    {
        Action act = () => new PairingService(new FakePairingClient(), new FakeBrowserLauncher(), new FakeDeviceKeyStore(), null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task PairAsync_HappyPath_OpensBrowserPollsAndSavesKey()
    {
        var pairing = new FakePairingClient(
            new PairComplete { Approved = false },
            new PairComplete { Approved = false },
            new PairComplete { Approved = true, ApiKey = "k" });
        pairing.StartResult = new PairStart { PairingId = "pid-7", Code = "AB12" };
        var browser = new FakeBrowserLauncher();
        var keys = new FakeDeviceKeyStore();
        var service = new PairingService(pairing, browser, keys, Settings());

        var outcome = await service.PairAsync(pollInterval: TimeSpan.FromMilliseconds(1), maxAttempts: 5);

        pairing.StartCallCount.Should().Be(1);
        pairing.LastDeviceName.Should().Be("Test-Device");
        browser.OpenCallCount.Should().Be(1);
        browser.LastUrl.Should().Be("https://srv.example.com/connect");
        keys.SavedKey.Should().Be("k");
        outcome.Success.Should().BeTrue();
        outcome.ApiKey.Should().Be("k");
        outcome.Code.Should().Be("AB12");
    }

    [Fact]
    public async Task PairAsync_Timeout_ReturnsFailureAndSavesNoKey()
    {
        var pairing = new FakePairingClient(new PairComplete { Approved = false });
        var browser = new FakeBrowserLauncher();
        var keys = new FakeDeviceKeyStore();
        var service = new PairingService(pairing, browser, keys, Settings());

        var outcome = await service.PairAsync(pollInterval: TimeSpan.FromMilliseconds(1), maxAttempts: 5);

        outcome.Success.Should().BeFalse();
        outcome.Message.Should().Contain("timed out");
        keys.SavedKey.Should().BeNull();
    }

    [Fact]
    public async Task EnsurePairedAsync_AlreadyPaired_ReturnsExistingKeyWithoutStarting()
    {
        var pairing = new FakePairingClient(new PairComplete { Approved = true, ApiKey = "fresh" });
        var browser = new FakeBrowserLauncher();
        var keys = new FakeDeviceKeyStore { Stored = "existing" };
        var service = new PairingService(pairing, browser, keys, Settings());

        var outcome = await service.EnsurePairedAsync();

        outcome.Success.Should().BeTrue();
        outcome.ApiKey.Should().Be("existing");
        pairing.StartCallCount.Should().Be(0);
        browser.OpenCallCount.Should().Be(0);
    }

    [Fact]
    public async Task EnsurePairedAsync_NotPaired_DelegatesToPairing()
    {
        var pairing = new FakePairingClient(new PairComplete { Approved = true, ApiKey = "new-key" });
        var browser = new FakeBrowserLauncher();
        var keys = new FakeDeviceKeyStore { Stored = null };
        var service = new PairingService(pairing, browser, keys, Settings());

        var outcome = await service.EnsurePairedAsync();

        outcome.Success.Should().BeTrue();
        outcome.ApiKey.Should().Be("new-key");
        pairing.StartCallCount.Should().Be(1);
        keys.SavedKey.Should().Be("new-key");
    }
}
