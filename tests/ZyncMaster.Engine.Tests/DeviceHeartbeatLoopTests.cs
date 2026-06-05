using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ZyncMaster.Core;
using ZyncMaster.Engine;
using Xunit;

namespace ZyncMaster.Engine.Tests;

// FIX C (client) — the device-lease heartbeat loop. BeatAsync is the unit-testable single tick:
// no device key -> clean no-op (not yet paired); a key -> exactly one HeartbeatAsync under that key.
public sealed class DeviceHeartbeatLoopTests
{
    private sealed class FakeKeyStore : IDeviceKeyStore
    {
        private string? _key;
        public FakeKeyStore(string? key) => _key = key;
        public Task SaveAsync(string apiKey, CancellationToken ct) { _key = apiKey; return Task.CompletedTask; }
        public Task<string?> LoadAsync(CancellationToken ct) => Task.FromResult(_key);
        public Task ClearAsync(CancellationToken ct) { _key = null; return Task.CompletedTask; }
    }

    // Records the api keys it was heartbeated with; can be configured to throw to prove the loop's
    // per-tick isolation (the public RunAsync swallows; BeatAsync propagates).
    private sealed class FakePairsClient : IPairsClient
    {
        public List<string> Heartbeats { get; } = new();
        public bool Throw { get; init; }

        public Task<DateTimeOffset?> HeartbeatAsync(string apiKey, CancellationToken ct)
        {
            Heartbeats.Add(apiKey);
            if (Throw) throw new InvalidOperationException("boom");
            return Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow.AddMinutes(10));
        }

        // Unused members for this test.
        public Task<IReadOnlyList<AccountInfo>> ListAccountsAsync(string bearer, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<CalendarInfo>> ListCalendarsAsync(string bearer, string accountRef, CancellationToken ct) => throw new NotImplementedException();
        public Task<CalendarInfo> CreateCalendarAsync(string bearer, string accountRef, string name, CancellationToken ct) => throw new NotImplementedException();
        public Task<SyncPair> CreatePairAsync(string bearer, string name, Endpoint source, Endpoint destination, int intervalMin, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<SyncPair>> ListPairsAsync(string bearer, CancellationToken ct) => throw new NotImplementedException();
        public Task<SyncPair> UpdatePairAsync(string bearer, string id, string? name, int? intervalMin, string? state, CancellationToken ct, Endpoint? source = null, Endpoint? destination = null) => throw new NotImplementedException();
        public Task<string> ExportSourceTxtAsync(string bearer, string id, int year, int month, bool includeCancelled, CancellationToken ct) => throw new NotImplementedException();
        public Task<CleanupResult> CleanupDestinationAsync(string bearer, string id, Endpoint oldDestination, CancellationToken ct) => throw new NotImplementedException();
        public Task<int> CountManagedAsync(string bearer, string id, Endpoint destination, CancellationToken ct) => throw new NotImplementedException();
        public Task DeletePairAsync(string bearer, string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> UnlinkAccountAsync(string bearer, string accountRef, CancellationToken ct) => throw new NotImplementedException();
        public Task<MirrorResult> PushPairAsync(string apiKey, string id, IReadOnlyList<AppointmentRecord> events, CancellationToken ct) => throw new NotImplementedException();
        public Task<MirrorResult> RunPairAsync(string apiKey, string id, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeviceInfo> GetDeviceMeAsync(string apiKey, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeviceInfo> RenameDeviceAsync(string apiKey, string name, CancellationToken ct) => throw new NotImplementedException();
        public Task<bool> CheckDeviceNameAvailableAsync(string apiKey, string name, CancellationToken ct) => throw new NotImplementedException();
    }

    [Fact]
    public async Task Beat_with_no_device_key_is_a_noop()
    {
        var client = new FakePairsClient();
        var loop = new DeviceHeartbeatLoop(client, new FakeKeyStore(null));

        await loop.BeatAsync(CancellationToken.None);

        client.Heartbeats.Should().BeEmpty("an unpaired device must not heartbeat");
    }

    [Fact]
    public async Task Beat_with_device_key_heartbeats_under_that_key()
    {
        var client = new FakePairsClient();
        var loop = new DeviceHeartbeatLoop(client, new FakeKeyStore("dev-key"));

        await loop.BeatAsync(CancellationToken.None);

        client.Heartbeats.Should().ContainSingle().Which.Should().Be("dev-key");
    }

    [Fact]
    public async Task Beat_propagates_failure_so_RunAsync_can_isolate_it()
    {
        var client = new FakePairsClient { Throw = true };
        var loop = new DeviceHeartbeatLoop(client, new FakeKeyStore("dev-key"));

        Func<Task> act = () => loop.BeatAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        client.Heartbeats.Should().ContainSingle("the failed beat still attempted the call");
    }

    [Fact]
    public void Nonpositive_interval_is_rejected()
    {
        Action act = () => new DeviceHeartbeatLoop(new FakePairsClient(), new FakeKeyStore("k"), TimeSpan.Zero);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Null_dependencies_are_rejected()
    {
        Action nullClient = () => new DeviceHeartbeatLoop(null!, new FakeKeyStore("k"));
        Action nullKeys = () => new DeviceHeartbeatLoop(new FakePairsClient(), null!);
        nullClient.Should().Throw<ArgumentNullException>();
        nullKeys.Should().Throw<ArgumentNullException>();
    }
}
