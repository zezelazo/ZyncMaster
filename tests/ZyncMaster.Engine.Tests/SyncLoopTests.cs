using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ZyncMaster.Engine;
using Xunit;

namespace ZyncMaster.Engine.Tests;

public sealed class SyncLoopTests
{
    private sealed class CountingCycle : ISyncCycle
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public bool ThrowOnFirst { get; set; }

        public Task<SyncResult> RunCycleAsync(CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref _count);
            if (ThrowOnFirst && n == 1)
                throw new InvalidOperationException("boom");
            return Task.FromResult(new SyncResult());
        }
    }

    [Fact]
    public void Ctor_NullCycle_Throws()
    {
        Action act = () => new SyncLoop(null!, TimeSpan.FromSeconds(1));
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_ZeroInterval_Throws()
    {
        Action act = () => new SyncLoop(new CountingCycle(), TimeSpan.Zero);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Ctor_NegativeInterval_Throws()
    {
        Action act = () => new SyncLoop(new CountingCycle(), TimeSpan.FromSeconds(-1));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task RunAsync_RunsRepeatedlyUntilCancelled()
    {
        var cycle = new CountingCycle();
        var loop = new SyncLoop(cycle, TimeSpan.FromMilliseconds(15));
        using var cts = new CancellationTokenSource();

        var run = loop.RunAsync(cts.Token);

        // Wait long enough for at least two cycles (immediate + at least one tick).
        await WaitUntilAsync(() => cycle.Count >= 2, TimeSpan.FromSeconds(5));

        cts.Cancel();
        Func<Task> act = () => run;
        await act.Should().NotThrowAsync();

        cycle.Count.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task RunAsync_CycleThatThrows_DoesNotKillLoop()
    {
        var cycle = new CountingCycle { ThrowOnFirst = true };
        var loop = new SyncLoop(cycle, TimeSpan.FromMilliseconds(15));
        using var cts = new CancellationTokenSource();

        var run = loop.RunAsync(cts.Token);

        // The first cycle throws; the loop must keep ticking past it.
        await WaitUntilAsync(() => cycle.Count >= 3, TimeSpan.FromSeconds(5));

        cts.Cancel();
        Func<Task> act = () => run;
        await act.Should().NotThrowAsync();

        cycle.Count.Should().BeGreaterThanOrEqualTo(3);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(5);
        }
    }
}
