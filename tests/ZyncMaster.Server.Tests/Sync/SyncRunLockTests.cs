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

    // FIX 2 — renewal extends a lock's expiry: a lock acquired with a SHORT ttl that is then renewed
    // with a long ttl must keep blocking a second acquire, proving the renewal pushed LockedUntil
    // forward (without renewal the short ttl would have lapsed and the second acquire would win).
    [Fact]
    public async Task Renew_extends_expiry_so_a_run_longer_than_ttl_keeps_the_lock()
    {
        using var harness = new EfStoreTestHarness();
        var sut = new EfSyncRunLock(harness.Factory);

        // Acquire with an already-elapsed ttl (the row is born expired), then RENEW with a long ttl.
        var handle = await sut.TryAcquireAsync("pair-1", TimeSpan.FromMilliseconds(-1), owner: "A");
        handle.Should().NotBeNull();

        var renewed = await handle!.RenewAsync(TimeSpan.FromMinutes(8));
        renewed.Should().BeTrue("we still own the row, so the renewal lands");

        // After renewal the lock is live again: a second executor cannot acquire it.
        var second = await sut.TryAcquireAsync("pair-1", TimeSpan.FromMinutes(8), owner: "B");
        second.Should().BeNull("renewal kept the lock alive past its original (elapsed) ttl");

        await handle.DisposeAsync();
    }

    // FIX 2 — once a lock has expired AND been stolen, the original holder's RenewAsync returns false
    // (its fence no longer matches) and does NOT extend the new owner's lock.
    [Fact]
    public async Task Renew_after_lock_was_stolen_returns_false_and_does_not_extend()
    {
        using var harness = new EfStoreTestHarness();
        var sut = new EfSyncRunLock(harness.Factory);

        var aHandle = await sut.TryAcquireAsync("pair-1", TimeSpan.FromMilliseconds(-1), owner: "A");
        aHandle.Should().NotBeNull();

        var bHandle = await sut.TryAcquireAsync("pair-1", TimeSpan.FromMinutes(8), owner: "B");
        bHandle.Should().NotBeNull("B steals A's expired lock");

        var renewed = await aHandle!.RenewAsync(TimeSpan.FromMinutes(30));
        renewed.Should().BeFalse("A lost the lock; its renewal must not touch B's row");

        // B still holds it; a third acquire still fails.
        var thief = await sut.TryAcquireAsync("pair-1", TimeSpan.FromMinutes(8), owner: "C");
        thief.Should().BeNull();

        await bHandle!.DisposeAsync();
    }

    // FIX 2 — in-memory store must enforce the same renewal semantics as the EF store.
    [Fact]
    public async Task InMemory_renew_extends_then_loses_after_steal()
    {
        var sut = new InMemorySyncRunLock();

        var handle = await sut.TryAcquireAsync("pair-1", TimeSpan.FromMilliseconds(-1), owner: "A");
        handle.Should().NotBeNull();
        (await handle!.RenewAsync(TimeSpan.FromMinutes(8))).Should().BeTrue();
        (await sut.TryAcquireAsync("pair-1", TimeSpan.FromMinutes(8), owner: "B"))
            .Should().BeNull("in-memory renewal also keeps the lock alive");
        await handle.DisposeAsync();

        // After release a fresh acquire wins; renew on the disposed handle is false.
        (await handle.RenewAsync(TimeSpan.FromMinutes(8))).Should().BeFalse();
    }

    // FIX 2 — the heartbeat interval is a fraction of the TTL, floored at the minimum, so the
    // executor renews several times before a long mirror could outlive the lock.
    [Fact]
    public void Heartbeat_interval_is_a_fraction_of_ttl_floored_at_minimum()
    {
        SyncRunLockHeartbeat.ComputeInterval(TimeSpan.FromMinutes(9))
            .Should().Be(TimeSpan.FromMinutes(3), "a 9-minute TTL renews every 3 minutes");

        SyncRunLockHeartbeat.ComputeInterval(TimeSpan.FromSeconds(3))
            .Should().Be(SyncRunLockHeartbeat.MinInterval, "a tiny TTL floors at the minimum interval");
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
