using System.Linq;
using FluentAssertions;
using Xunit;

namespace ZyncMaster.Server.Postgres.Tests;

[Collection(PostgresCollection.Name)]
public sealed class SchemaMigratesTests
{
    private readonly PostgresFixture _pg;
    public SchemaMigratesTests(PostgresFixture pg) => _pg = pg;

    [SkippableFact]
    public void InitialCreate_BuildsSchema_AndContextConnects()
    {
        Skip.IfNot(_pg.Available, "No PostgreSQL (set ZYNCMASTER_TEST_PG).");
        using var db = _pg.NewContext();
        db.Database.CanConnect().Should().BeTrue();
        // A trivial query against a migrated table proves the DDL ran.
        db.Devices.Count().Should().BeGreaterThanOrEqualTo(0);
    }

    [SkippableFact]
    public void CalendarV2_ReplicaTables_Migrate_AndAreQueryable()
    {
        Skip.IfNot(_pg.Available, "No PostgreSQL (set ZYNCMASTER_TEST_PG).");
        using var db = _pg.NewContext();
        // A trivial query against each new table proves the CalendarV2Replicas DDL ran.
        db.ReplicaLinks.Count().Should().BeGreaterThanOrEqualTo(0);
        db.PrefixRules.Count().Should().BeGreaterThanOrEqualTo(0);
        db.PrefixRuleDestinations.Count().Should().BeGreaterThanOrEqualTo(0);
    }
}
