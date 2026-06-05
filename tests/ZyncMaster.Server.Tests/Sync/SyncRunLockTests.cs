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

    // FIX B — a tardy Dispose by a holder whose lock already EXPIRED and was legitimately stolen
    // must NOT free the new owner's live lock (which would let two destructive mirrors run on the
    // same calendar). The fencing token makes release match only the row the holder still owns.
    [Fact]
    public async Task Late_dispose_of_stolen_lock_does_not_release_new_owners_lock()
    {
        using var harness = new EfStoreTestHarness();
        var sut = new EfSyncRunLock(harness.Factory);

        // Owner A acquires with an already-elapsed TTL: its lock exists but is immediately expired.
        var aHandle = await sut.TryAcquireAsync("pair-1", TimeSpan.FromMilliseconds(-1), owner: "A");
        aHandle.Should().NotBeNull();

        // Owner B legitimately STEALS the expired lock (the §B-1 `LockedUntil < now` gate) and holds
        // it live. B now owns the row, with its own fence token.
        var bHandle = await sut.TryAcquireAsync("pair-1", TimeSpan.FromMinutes(8), owner: "B");
        bHandle.Should().NotBeNull("B may steal A's expired lock");

        // A's late Dispose fires AFTER B stole the lock. It must be a no-op (its fence no longer
        // matches), so B's live lock survives and a third executor still cannot acquire.
        await aHandle!.DisposeAsync();

        var thief = await sut.TryAcquireAsync("pair-1", TimeSpan.FromMinutes(8), owner: "C");
        thief.Should().BeNull("A's late Dispose must not free the lock B now holds");

        await bHandle!.DisposeAsync();
    }

    // FIX B — in-memory store must enforce the same fencing semantics as the EF store.
    [Fact]
    public async Task InMemory_late_dispose_of_stolen_lock_does_not_release_new_owners_lock()
    {
        var sut = new InMemorySyncRunLock();

        var aHandle = await sut.TryAcquireAsync("pair-1", TimeSpan.FromMilliseconds(-1), owner: "A");
        aHandle.Should().NotBeNull();

        var bHandle = await sut.TryAcquireAsync("pair-1", TimeSpan.FromMinutes(8), owner: "B");
        bHandle.Should().NotBeNull();

        await aHandle!.DisposeAsync();

        var thief = await sut.TryAcquireAsync("pair-1", TimeSpan.FromMinutes(8), owner: "C");
        thief.Should().BeNull("A's late Dispose must not free the lock B now holds (in-memory)");

        await bHandle!.DisposeAsync();
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
