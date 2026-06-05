using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ZyncMaster.Core;
using ZyncMaster.Engine;
using Xunit;

namespace ZyncMaster.Engine.Tests;

public sealed class SyncEngineTests
{
    private sealed class FakeDeviceKeyStore : IDeviceKeyStore
    {
        public string? Stored { get; set; }
        public Task SaveAsync(string apiKey, CancellationToken ct) { Stored = apiKey; return Task.CompletedTask; }
        public Task<string?> LoadAsync(CancellationToken ct) => Task.FromResult(Stored);
        public Task ClearAsync(CancellationToken ct) { Stored = null; return Task.CompletedTask; }
    }

    private sealed class FakeCalendarSource : ICalendarSource
    {
        public bool Called { get; private set; }
        public DateTimeOffset FromUtc { get; private set; }
        public DateTimeOffset ToUtc { get; private set; }
        public IReadOnlyList<AppointmentRecord> Result { get; set; } = Array.Empty<AppointmentRecord>();
        public Exception? Throw { get; set; }

        public IReadOnlyList<string>? Selection { get; private set; }

        public Task<IReadOnlyList<AppointmentRecord>> ReadWindowAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, IReadOnlyList<string>? calendarNames, CancellationToken ct)
        {
            Called = true;
            FromUtc = fromUtc;
            ToUtc = toUtc;
            Selection = calendarNames;
            if (Throw != null) throw Throw;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeSyncClient : ISyncClient
    {
        public bool Called { get; private set; }
        public string? LastApiKey { get; private set; }
        public IReadOnlyList<AppointmentRecord>? LastEvents { get; private set; }
        public SyncPushResult Result { get; set; } = new SyncPushResult();

        public Task<SyncPushResult> PushAsync(string apiKey, IReadOnlyList<AppointmentRecord> events, CancellationToken ct)
        {
            Called = true;
            LastApiKey = apiKey;
            LastEvents = events;
            return Task.FromResult(Result);
        }
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset now) => UtcNow = now;
        public DateTimeOffset UtcNow { get; }
    }

    private static EngineSettings Settings(int windowDays = 14) => new EngineSettings
    {
        ServerBaseUrl = "https://srv.example.com",
        SyncWindowDays = windowDays,
    };

    [Fact]
    public void Ctor_NullKeys_Throws()
    {
        Action act = () => new SyncEngine(null!, new FakeCalendarSource(), new FakeSyncClient(), new FixedClock(DateTimeOffset.UtcNow), Settings());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullSource_Throws()
    {
        Action act = () => new SyncEngine(new FakeDeviceKeyStore(), null!, new FakeSyncClient(), new FixedClock(DateTimeOffset.UtcNow), Settings());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullClient_Throws()
    {
        Action act = () => new SyncEngine(new FakeDeviceKeyStore(), new FakeCalendarSource(), null!, new FixedClock(DateTimeOffset.UtcNow), Settings());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullClock_Throws()
    {
        Action act = () => new SyncEngine(new FakeDeviceKeyStore(), new FakeCalendarSource(), new FakeSyncClient(), null!, Settings());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullSettings_Throws()
    {
        Action act = () => new SyncEngine(new FakeDeviceKeyStore(), new FakeCalendarSource(), new FakeSyncClient(), new FixedClock(DateTimeOffset.UtcNow), null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task RunCycleAsync_NoKey_SkipsWithoutTouchingSourceOrClient()
    {
        var source = new FakeCalendarSource();
        var client = new FakeSyncClient();
        var engine = new SyncEngine(new FakeDeviceKeyStore { Stored = null }, source, client, new FixedClock(DateTimeOffset.UtcNow), Settings());

        var result = await engine.RunCycleAsync();

        result.Skipped.Should().BeTrue();
        result.SkipReason.Should().Contain("not paired");
        source.Called.Should().BeFalse();
        client.Called.Should().BeFalse();
    }

    [Fact]
    public async Task RunCycleAsync_Happy_ReadsWindowAndPushesWithKey()
    {
        var now = new DateTimeOffset(2026, 5, 26, 12, 0, 0, TimeSpan.Zero);
        var source = new FakeCalendarSource();
        var client = new FakeSyncClient { Result = new SyncPushResult { Created = 3 } };
        var engine = new SyncEngine(new FakeDeviceKeyStore { Stored = "the-key" }, source, client, new FixedClock(now), Settings(windowDays: 14));

        var result = await engine.RunCycleAsync();

        source.FromUtc.Should().Be(now);
        source.ToUtc.Should().Be(now.AddDays(14));
        client.LastApiKey.Should().Be("the-key");
        result.Skipped.Should().BeFalse();
        result.Push.Should().NotBeNull();
        result.Push!.Created.Should().Be(3);
    }

    [Fact]
    public async Task RunCycleAsync_NoConnectedAccount_SurfacesFlag()
    {
        var source = new FakeCalendarSource();
        var client = new FakeSyncClient { Result = new SyncPushResult { NoConnectedAccount = true } };
        var engine = new SyncEngine(new FakeDeviceKeyStore { Stored = "k" }, source, client, new FixedClock(DateTimeOffset.UtcNow), Settings());

        var result = await engine.RunCycleAsync();

        result.Skipped.Should().BeFalse();
        result.Push.Should().NotBeNull();
        result.Push!.NoConnectedAccount.Should().BeTrue();
    }

    [Fact]
    public async Task RunCycleAsync_SourceThrows_SkipsWithReasonNoExceptionEscapes()
    {
        var source = new FakeCalendarSource { Throw = new InvalidOperationException("outlook is closed") };
        var client = new FakeSyncClient();
        var engine = new SyncEngine(new FakeDeviceKeyStore { Stored = "k" }, source, client, new FixedClock(DateTimeOffset.UtcNow), Settings());

        var result = await engine.RunCycleAsync();

        result.Skipped.Should().BeTrue();
        result.SkipReason.Should().Contain("outlook is closed");
        client.Called.Should().BeFalse();
    }
}
