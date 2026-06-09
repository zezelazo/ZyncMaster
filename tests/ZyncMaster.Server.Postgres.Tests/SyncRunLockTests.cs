using System;
using System.Threading.Tasks;
using FluentAssertions;
using ZyncMaster.Server;
using Xunit;

namespace ZyncMaster.Server.Postgres.Tests;

// Exercises EfSyncRunLock against a REAL PostgreSQL. This is the path SQLite cannot represent:
// the acquire/renew/release raw SQL must use quoted identifiers (PostgreSQL folds unquoted names
// to lowercase, so an unquoted "SyncRunLocks" never matches the quoted table), and the busy-lock
// branch is driven by a real Npgsql.PostgresException 23505 on the INSERT PK collision. A bug in
// any of those four statements fails here even though the SQLite suite stays green.
[Collection(PostgresCollection.Name)]
public sealed class SyncRunLockTests
{
    private readonly PostgresFixture _pg;
    public SyncRunLockTests(PostgresFixture pg) => _pg = pg;

    [SkippableFact]
    public async Task Acquire_Renew_Release_RoundTrip_OnPostgres()
    {
        Skip.IfNot(_pg.Available, "No PostgreSQL (set ZYNCMASTER_TEST_PG).");
        var sut = new EfSyncRunLock(_pg.Factory);
        var pairId = "pair-" + Guid.NewGuid().ToString("N");

        // Acquire: the quoted UPDATE matches nothing (no row yet), then the quoted INSERT creates it.
        var handle = await sut.TryAcquireAsync(pairId, TimeSpan.FromMinutes(5), owner: "host-a");
        handle.Should().NotBeNull("the first acquire on a fresh pair must succeed");

        // Renew: the quoted fenced UPDATE extends our live lock.
        (await handle!.RenewAsync(TimeSpan.FromMinutes(5))).Should().BeTrue();

        // Release: the quoted fenced UPDATE frees the row for the next acquire.
        await handle.DisposeAsync();

        // Re-acquire after release succeeds (the steal-UPDATE path: LockedUntil now in the past).
        var again = await sut.TryAcquireAsync(pairId, TimeSpan.FromMinutes(5), owner: "host-a");
        again.Should().NotBeNull("after release the lock is free again");
        await again!.DisposeAsync();
    }

    [SkippableFact]
    public async Task SecondAcquire_WhileLive_IsBlocked_ViaReal23505()
    {
        Skip.IfNot(_pg.Available, "No PostgreSQL (set ZYNCMASTER_TEST_PG).");
        var sut = new EfSyncRunLock(_pg.Factory);
        var pairId = "pair-" + Guid.NewGuid().ToString("N");

        await using var first = await sut.TryAcquireAsync(pairId, TimeSpan.FromMinutes(10), owner: "host-a");
        first.Should().NotBeNull();

        // A second acquire while the first is live: the steal-UPDATE matches 0 rows (lock not
        // expired), the INSERT collides on the PairId primary key -> Npgsql.PostgresException 23505,
        // caught by IsUniqueViolation -> "not acquired" (null), never an unhandled throw.
        var second = await sut.TryAcquireAsync(pairId, TimeSpan.FromMinutes(10), owner: "host-b");
        second.Should().BeNull("a live lock held by host-a blocks host-b through the 23505 path");
    }
}
