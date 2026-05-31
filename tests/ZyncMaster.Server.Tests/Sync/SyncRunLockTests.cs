using System;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using ZyncMaster.Server;
using ZyncMaster.Server.Tests.Storage;
using Xunit;

namespace ZyncMaster.Server.Tests.Sync;

// Mechanics of the per-pair run lock (plan v2 §B-1). Uses the EF SQLite harness (shared
// in-memory connection) so the atomic `UPDATE ... WHERE LockedUntil < now` runs against a
// real relational engine — the same atomicity the production SQL Server path relies on.
public sealed class SyncRunLockTests
{
    [Fact]
    public async Task Acquires_when_free()
    {
        using var harness = new EfStoreTestHarness();
        var sut = new EfSyncRunLock(harness.Factory);

        await using var handle = await sut.TryAcquireAsync("pair-1", TimeSpan.FromMinutes(8));

        handle.Should().NotBeNull();
        handle!.PairId.Should().Be("pair-1");
    }

    [Fact]
    public async Task Live_lock_blocks_second_acquire()
    {
        using var harness = new EfStoreTestHarness();
        var sut = new EfSyncRunLock(harness.Factory);

        await using var first = await sut.TryAcquireAsync("pair-1", TimeSpan.FromMinutes(8));
        first.Should().NotBeNull();

        var second = await sut.TryAcquireAsync("pair-1", TimeSpan.FromMinutes(8));
        second.Should().BeNull("a live lock must block a second acquire");
    }

    [Fact]
    public async Task Release_allows_reacquire()
    {
        using var harness = new EfStoreTestHarness();
        var sut = new EfSyncRunLock(harness.Factory);

        var first = await sut.TryAcquireAsync("pair-1", TimeSpan.FromMinutes(8));
        first.Should().NotBeNull();
        await first!.DisposeAsync(); // release

        await using var second = await sut.TryAcquireAsync("pair-1", TimeSpan.FromMinutes(8));
        second.Should().NotBeNull("a released lock must be re-acquirable");
    }

    [Fact]
    public async Task Expired_lock_can_be_reacquired()
    {
        using var harness = new EfStoreTestHarness();
        var sut = new EfSyncRunLock(harness.Factory);

        // Acquire with an already-elapsed TTL: the lock row exists but is past LockedUntil.
        var stale = await sut.TryAcquireAsync("pair-1", TimeSpan.FromMilliseconds(-1));
        stale.Should().NotBeNull();

        await using var fresh = await sut.TryAcquireAsync("pair-1", TimeSpan.FromMinutes(8));
        fresh.Should().NotBeNull("an expired lock must be stealable by the next run");
    }

    [Fact]
    public async Task Different_pairs_do_not_contend()
    {
        using var harness = new EfStoreTestHarness();
        var sut = new EfSyncRunLock(harness.Factory);

        await using var a = await sut.TryAcquireAsync("pair-a", TimeSpan.FromMinutes(8));
        await using var b = await sut.TryAcquireAsync("pair-b", TimeSpan.FromMinutes(8));

        a.Should().NotBeNull();
        b.Should().NotBeNull("locks are per-pair; distinct pairs never contend");
    }

    [Fact]
    public async Task Concurrent_acquire_lets_exactly_one_win()
    {
        using var harness = new EfStoreTestHarness();
        var sut = new EfSyncRunLock(harness.Factory);

        // Fire many parallel acquisitions of the SAME pair. The atomic UPDATE/INSERT must
        // let exactly one through — the destructive-mirror mutual exclusion.
        var tasks = Enumerable.Range(0, 16)
            .Select(_ => sut.TryAcquireAsync("pair-hot", TimeSpan.FromMinutes(8)))
            .ToArray();

        var handles = await Task.WhenAll(tasks);

        handles.Count(h => h is not null).Should().Be(1, "exactly one concurrent acquire may win");

        foreach (var h in handles.Where(h => h is not null))
            await h!.DisposeAsync();
    }
}
