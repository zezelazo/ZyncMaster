using System;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;
using ZyncMaster.Server;
using ZyncMaster.Server.Data;

namespace ZyncMaster.Server.Postgres.Tests;

[Collection(PostgresCollection.Name)]
public sealed class UniqueViolationTests
{
    private readonly PostgresFixture _pg;
    public UniqueViolationTests(PostgresFixture pg) => _pg = pg;

    [SkippableFact]
    public void DuplicateDeviceName_RaisesPostgres23505_ClassifiedAsConflict()
    {
        Skip.IfNot(_pg.Available, "No PostgreSQL (set ZYNCMASTER_TEST_PG).");

        // Arrange: two devices that collide on the unique (UserId, NameLower) index. The
        // DbContext override derives NameLower from Name on every write, so identical Name +
        // UserId is what fires the real PostgreSQL unique_violation.
        var userId = DefaultCurrentUserAccessor.DefaultUserId;
        var now = DateTimeOffset.UtcNow;

        DeviceRow MakeDevice() => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId,
            Name = "Frodo",            // NameLower derived to "frodo" by SaveChanges
            ApiKeyHash = "hash",
            CreatedUtc = now,
            Platform = "windows",
        };

        using (var seed = _pg.NewContext())
        {
            seed.Devices.Add(MakeDevice());
            seed.SaveChanges();
        }

        using var db = _pg.NewContext();
        db.Devices.Add(MakeDevice()); // same UserId + Name => duplicate (UserId, NameLower)

        Action insertDuplicate = () => db.SaveChanges();

        // Assert: the real PostgresException(23505) wrapped in DbUpdateException is recognised
        // by DeviceService.IsNameConflict — the dialect path SQLite cannot represent.
        insertDuplicate.Should().Throw<DbUpdateException>()
            .Which.Should().Match<DbUpdateException>(ex => DeviceService.IsNameConflict(ex));
    }
}
