using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ZyncMaster.Core;
using ZyncMaster.Engine;
using Xunit;

namespace ZyncMaster.Engine.Tests;

public sealed class PairSchedulerTests
{
    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2025, 5, 10, 8, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan d) => UtcNow += d;
    }

    private sealed class FakeKeyStore : IDeviceKeyStore
    {
        private string? _key;
        public FakeKeyStore(string? key) => _key = key;
        public Task SaveAsync(string apiKey, CancellationToken ct) { _key = apiKey; return Task.CompletedTask; }
        public Task<string?> LoadAsync(CancellationToken ct) => Task.FromResult(_key);
        public Task ClearAsync(CancellationToken ct) { _key = null; return Task.CompletedTask; }
    }

    private sealed class FakeIdentityTokenProvider : IIdentityTokenProvider
    {
        private readonly string? _token;
        public FakeIdentityTokenProvider(string? token) => _token = token;
        public Task<string?> LoadAccessTokenAsync(CancellationToken ct = default) => Task.FromResult(_token);
    }

    private sealed class FakeSource : ICalendarSource
    {
        public int ReadCount;
        public DateTimeOffset LastFrom;
        public DateTimeOffset LastTo;
        public Task<IReadOnlyList<AppointmentRecord>> ReadWindowAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
        {
            Interlocked.Increment(ref ReadCount);
            LastFrom = fromUtc;
            LastTo = toUtc;
            IReadOnlyList<AppointmentRecord> list = new[] { new AppointmentRecord { Id = "e1", Subject = "x" } };
            return Task.FromResult(list);
        }
    }

    private sealed class FakePairsClient : IPairsClient
    {
        public List<SyncPair> Pairs = new();
        // ListPairs is authenticated with the identity BEARER; push/run with the device API KEY.
        public string? LastListBearer;
        public string? LastPushApiKey;
        public string? LastRunApiKey;
        public List<string> PushedPairIds = new();
        public List<string> RanPairIds = new();
        public HashSet<string> ThrowOnRunPairIds = new();

        public Task<IReadOnlyList<AccountInfo>> ListAccountsAsync(string bearer, CancellationToken ct)
            => Task.FromResult((IReadOnlyList<AccountInfo>)Array.Empty<AccountInfo>());
        public Task<IReadOnlyList<CalendarInfo>> ListCalendarsAsync(string bearer, string accountRef, CancellationToken ct)
            => Task.FromResult((IReadOnlyList<CalendarInfo>)Array.Empty<CalendarInfo>());
        public Task<SyncPair> CreatePairAsync(string bearer, string name, Endpoint source, Endpoint destination, int intervalMin, CancellationToken ct)
            => Task.FromResult(new SyncPair());
        public Task<IReadOnlyList<SyncPair>> ListPairsAsync(string bearer, CancellationToken ct)
        {
            LastListBearer = bearer;
            return Task.FromResult((IReadOnlyList<SyncPair>)Pairs.ToList());
        }
        public Task<SyncPair> UpdatePairAsync(string bearer, string id, string? name, int? intervalMin, string? state, CancellationToken ct)
            => Task.FromResult(new SyncPair());
        public Task DeletePairAsync(string bearer, string id, CancellationToken ct) => Task.CompletedTask;
        public Task<MirrorResult> PushPairAsync(string apiKey, string id, IReadOnlyList<AppointmentRecord> events, CancellationToken ct)
        {
            LastPushApiKey = apiKey;
            PushedPairIds.Add(id);
            return Task.FromResult(new MirrorResult { Created = 1 });
        }
        public Task<MirrorResult> RunPairAsync(string apiKey, string id, CancellationToken ct)
        {
            LastRunApiKey = apiKey;
            RanPairIds.Add(id);
            if (ThrowOnRunPairIds.Contains(id))
                throw new InvalidOperationException("boom " + id);
            return Task.FromResult(new MirrorResult { Updated = 1 });
        }
        public Task<IReadOnlyList<string>> UnlinkAccountAsync(string bearer, string accountRef, CancellationToken ct)
            => Task.FromResult((IReadOnlyList<string>)Array.Empty<string>());
        public Task<DeviceInfo> GetDeviceMeAsync(string apiKey, CancellationToken ct)
            => Task.FromResult(new DeviceInfo());
        public Task<DeviceInfo> RenameDeviceAsync(string apiKey, string name, CancellationToken ct)
            => Task.FromResult(new DeviceInfo { Name = name });
    }

    private static SyncPair Pair(string id, string state, int intervalMin, string sourceProvider)
        => new SyncPair
        {
            Id = id,
            Name = id,
            State = state,
            IntervalMin = intervalMin,
            Source = new Endpoint { Provider = sourceProvider, CalendarId = "c", CalendarName = "C" },
            Destination = new Endpoint { Provider = "MicrosoftGraph", AccountRef = "a", CalendarId = "d", CalendarName = "D" },
        };

    private static EngineSettings Settings() => new EngineSettings
    {
        ServerBaseUrl = "https://srv.example.com",
        SyncWindowDays = 14,
    };

    // Default fakes carry a device key "k" and an identity bearer "b" so the tick reaches the
    // pair list (bearer) and then pushes/runs (key).
    private static PairScheduler Make(FakePairsClient client, FakeSource source, MutableClock clock, FakeKeyStore keys)
        => new PairScheduler(client, source, keys, new FakeIdentityTokenProvider("b"), clock, Settings());

    [Fact]
    public void Ctor_NullClient_Throws()
    {
        Action act = () => new PairScheduler(null!, new FakeSource(), new FakeKeyStore("k"), new FakeIdentityTokenProvider("b"), new MutableClock(), Settings());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullIdentity_Throws()
    {
        Action act = () => new PairScheduler(new FakePairsClient(), new FakeSource(), new FakeKeyStore("k"), null!, new MutableClock(), Settings());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullSettings_Throws()
    {
        Action act = () => new PairScheduler(new FakePairsClient(), new FakeSource(), new FakeKeyStore("k"), new FakeIdentityTokenProvider("b"), new MutableClock(), null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task Tick_ActiveComPair_ReadsWindowAndPushes()
    {
        var client = new FakePairsClient { Pairs = { Pair("p1", "active", 10, "OutlookCom") } };
        var source = new FakeSource();
        var clock = new MutableClock();
        var sched = Make(client, source, clock, new FakeKeyStore("k"));

        await sched.TickAsync(CancellationToken.None);

        // List used the identity bearer; the COM push used the device api key.
        client.LastListBearer.Should().Be("b");
        client.LastPushApiKey.Should().Be("k");
        source.ReadCount.Should().Be(1);
        source.LastFrom.Should().Be(clock.UtcNow);
        source.LastTo.Should().Be(clock.UtcNow.AddDays(14));
        client.PushedPairIds.Should().ContainSingle().Which.Should().Be("p1");
        client.RanPairIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Tick_ActiveGraphPair_CallsRunPairNotPush()
    {
        var client = new FakePairsClient { Pairs = { Pair("p1", "active", 10, "MicrosoftGraph") } };
        var source = new FakeSource();
        var clock = new MutableClock();
        var sched = Make(client, source, clock, new FakeKeyStore("k"));

        await sched.TickAsync(CancellationToken.None);

        client.RanPairIds.Should().ContainSingle().Which.Should().Be("p1");
        client.PushedPairIds.Should().BeEmpty();
        source.ReadCount.Should().Be(0);
        // Listing used the bearer; the run used the device api key.
        client.LastListBearer.Should().Be("b");
        client.LastRunApiKey.Should().Be("k");
    }

    [Fact]
    public async Task Tick_PausedAndDisabledPairs_AreSkipped()
    {
        var client = new FakePairsClient
        {
            Pairs =
            {
                Pair("paused", "paused", 10, "MicrosoftGraph"),
                Pair("disabled", "disabled", 10, "MicrosoftGraph"),
            },
        };
        var sched = Make(client, new FakeSource(), new MutableClock(), new FakeKeyStore("k"));

        await sched.TickAsync(CancellationToken.None);

        client.RanPairIds.Should().BeEmpty();
        client.PushedPairIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Tick_TwoPairsDifferentIntervals_RunWhenDue()
    {
        var client = new FakePairsClient
        {
            Pairs =
            {
                Pair("fast", "active", 10, "MicrosoftGraph"),
                Pair("slow", "active", 30, "MicrosoftGraph"),
            },
        };
        var clock = new MutableClock();
        var sched = Make(client, new FakeSource(), clock, new FakeKeyStore("k"));

        // First tick: both run once (no prior run recorded).
        await sched.TickAsync(CancellationToken.None);
        client.RanPairIds.Should().BeEquivalentTo(new[] { "fast", "slow" });

        // +11 min: only the 10-min pair is due again.
        clock.Advance(TimeSpan.FromMinutes(11));
        await sched.TickAsync(CancellationToken.None);
        client.RanPairIds.Should().BeEquivalentTo(new[] { "fast", "slow", "fast" });

        // +another 11 min (22 total): fast due again; slow (30 min) still not due.
        clock.Advance(TimeSpan.FromMinutes(11));
        await sched.TickAsync(CancellationToken.None);
        client.RanPairIds.Where(x => x == "slow").Should().HaveCount(1);
        client.RanPairIds.Where(x => x == "fast").Should().HaveCount(3);

        // +10 min (32 total since slow's last run at t0): slow now due.
        clock.Advance(TimeSpan.FromMinutes(10));
        await sched.TickAsync(CancellationToken.None);
        client.RanPairIds.Where(x => x == "slow").Should().HaveCount(2);
    }

    [Fact]
    public async Task Tick_OnePairThrows_OtherStillRuns()
    {
        var client = new FakePairsClient
        {
            Pairs =
            {
                Pair("bad", "active", 10, "MicrosoftGraph"),
                Pair("good", "active", 10, "MicrosoftGraph"),
            },
            ThrowOnRunPairIds = { "bad" },
        };
        var sched = Make(client, new FakeSource(), new MutableClock(), new FakeKeyStore("k"));

        await sched.TickAsync(CancellationToken.None);

        client.RanPairIds.Should().Contain("bad");
        client.RanPairIds.Should().Contain("good");
    }

    [Fact]
    public async Task Tick_NoApiKey_DoesNothing()
    {
        var client = new FakePairsClient { Pairs = { Pair("p1", "active", 10, "MicrosoftGraph") } };
        var sched = Make(client, new FakeSource(), new MutableClock(), new FakeKeyStore(null));

        await sched.TickAsync(CancellationToken.None);

        client.LastListBearer.Should().BeNull();
        client.RanPairIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Tick_NoBearer_DoesNothing()
    {
        // Paired device but signed out: listing pairs needs the human identity bearer, so a tick
        // is a clean no-op (no list, no run).
        var client = new FakePairsClient { Pairs = { Pair("p1", "active", 10, "MicrosoftGraph") } };
        var sched = new PairScheduler(
            client, new FakeSource(), new FakeKeyStore("k"), new FakeIdentityTokenProvider(null),
            new MutableClock(), Settings());

        await sched.TickAsync(CancellationToken.None);

        client.LastListBearer.Should().BeNull();
        client.RanPairIds.Should().BeEmpty();
        client.PushedPairIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Tick_DeletedPair_DropsFromSchedule()
    {
        var client = new FakePairsClient { Pairs = { Pair("p1", "active", 10, "MicrosoftGraph") } };
        var clock = new MutableClock();
        var sched = Make(client, new FakeSource(), clock, new FakeKeyStore("k"));

        await sched.TickAsync(CancellationToken.None);
        client.RanPairIds.Should().ContainSingle();

        // Server reports the pair gone. After it's due again, it must not run.
        client.Pairs.Clear();
        clock.Advance(TimeSpan.FromMinutes(11));
        await sched.TickAsync(CancellationToken.None);

        client.RanPairIds.Should().ContainSingle();
    }

    [Fact]
    public async Task RunAsync_StopsCleanlyOnCancellation()
    {
        var client = new FakePairsClient { Pairs = { Pair("p1", "active", 10, "MicrosoftGraph") } };
        var sched = new PairScheduler(client, new FakeSource(), new FakeKeyStore("k"), new FakeIdentityTokenProvider("b"), new MutableClock(), Settings(),
            tickInterval: TimeSpan.FromMilliseconds(5));
        using var cts = new CancellationTokenSource();

        var run = sched.RunAsync(cts.Token);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (client.RanPairIds.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(5);

        cts.Cancel();
        Func<Task> act = () => run;
        await act.Should().NotThrowAsync();
        client.RanPairIds.Should().NotBeEmpty();
    }
}
