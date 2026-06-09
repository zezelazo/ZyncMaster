using System;
using Microsoft.EntityFrameworkCore;
using ZyncMaster.Server.Data;

namespace ZyncMaster.Server.Postgres.Tests;

// Builds the real PostgreSQL schema once for the test run using the InitialCreate migration,
// against a throwaway database named from ZYNCMASTER_TEST_PG. Null ConnectionString => no PG
// available => every test Skip.IfNot(_pg.Available, ...).
public sealed class PostgresFixture : IDisposable
{
    public string? ConnectionString { get; } =
        Environment.GetEnvironmentVariable("ZYNCMASTER_TEST_PG");

    public bool Available => !string.IsNullOrWhiteSpace(ConnectionString);

    public PostgresFixture()
    {
        if (!Available) return;
        using var db = NewContext();
        db.Database.EnsureDeleted();
        db.Database.Migrate(); // applies InitialCreate (DDL) — the real schema, as efbundle will.
    }

    public ZyncMasterDbContext NewContext()
    {
        var opts = new DbContextOptionsBuilder<ZyncMasterDbContext>()
            .UseNpgsql(ConnectionString!)
            .Options;
        return new ZyncMasterDbContext(opts);
    }

    public void Dispose()
    {
        if (!Available) return;
        using var db = NewContext();
        db.Database.EnsureDeleted();
    }
}
