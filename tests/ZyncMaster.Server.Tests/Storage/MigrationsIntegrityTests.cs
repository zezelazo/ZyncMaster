using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ZyncMaster.Server.Data;
using Xunit;

namespace ZyncMaster.Server.Tests.Storage;

// Guards against editing the EF model without adding a matching migration. The production
// migrations are generated for the PostgreSQL (Npgsql) provider, and the model snapshot under
// Data/Migrations is provider-specific, so the context here is configured with UseNpgsql
// (no real database is touched — HasPendingModelChanges compares the in-memory model against
// the latest migration snapshot only). If this fails, run:
//   dotnet ef migrations add <Name> -p src/ZyncMaster.Server -s src/ZyncMaster.Server
public class MigrationsIntegrityTests
{
    [Fact]
    public void Model_has_no_pending_changes_versus_latest_migration_snapshot()
    {
        var options = new DbContextOptionsBuilder<ZyncMasterDbContext>()
            // A syntactically valid connection string; HasPendingModelChanges does not open it.
            .UseNpgsql("Host=localhost;Port=5432;Database=ZyncMaster_IntegrityCheck;Username=check;Password=check")
            .Options;

        using var db = new ZyncMasterDbContext(options);

        db.Database.HasPendingModelChanges().Should().BeFalse(
            "the EF model was changed without adding a migration — run 'dotnet ef migrations add <Name>'.");
    }
}
