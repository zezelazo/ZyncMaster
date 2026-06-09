using Xunit;

namespace ZyncMaster.Server.Postgres.Tests;

// Shares ONE PostgresFixture across every integration test class. The fixture deletes and re-migrates
// the throwaway database in its constructor, so it must run exactly once for the whole assembly — a
// per-class fixture would let one class's EnsureDeleted (on dispose) race another class's Migrate and
// fail with 3D000 "database does not exist". A collection fixture also disables cross-class
// parallelism for these tests, which is correct: they share one physical database.
[CollectionDefinition(PostgresCollection.Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
