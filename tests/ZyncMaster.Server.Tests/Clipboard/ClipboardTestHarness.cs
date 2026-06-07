using System;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZyncMaster.Server;
using ZyncMaster.Server.Data;

namespace ZyncMaster.Server.Tests.Clipboard;

// Builds EfClipboardHistoryStore instances wired to a SQLite in-memory database and a
// fixed current user. Mirrors EfStoreTestHarness (shared open connection, schema created
// once) but lets each store run as a different user so user-scoping can be asserted.
//
// HistoryStore(userId, opts) creates a brand-new in-memory DB for that store.
// HistoryStore(userId, opts, shareDb: true) reuses a single process-wide shared DB so two
// stores under different users hit the same physical tables.
internal static class ClipboardTestHarness
{
    private sealed class FixedCurrentUser : ICurrentUserAccessor
    {
        public FixedCurrentUser(string userId) => UserId = userId;
        public string UserId { get; }
    }

    // Holds an open connection alive for the lifetime of the in-memory DB; closing the
    // connection drops the schema, so the harness keeps a static reference for the shared DB.
    private sealed class Db
    {
        public SqliteConnection Connection { get; }
        public IDbContextFactory<ZyncMasterDbContext> Factory { get; }

        public Db()
        {
            Connection = new SqliteConnection("DataSource=:memory:");
            Connection.Open();
            var options = new DbContextOptionsBuilder<ZyncMasterDbContext>()
                .UseSqlite(Connection)
                .Options;
            Factory = new SimpleFactory(options);
            using var db = Factory.CreateDbContext();
            db.Database.EnsureCreated();
        }

        private sealed class SimpleFactory : IDbContextFactory<ZyncMasterDbContext>
        {
            private readonly DbContextOptions<ZyncMasterDbContext> _options;
            public SimpleFactory(DbContextOptions<ZyncMasterDbContext> options) => _options = options;
            public ZyncMasterDbContext CreateDbContext() => new(_options);
        }
    }

    private static readonly object SharedLock = new();
    private static Db? _shared;

    public static IClipboardHistoryStore HistoryStore(
        string userId, ClipboardOptions? opts = null, bool shareDb = false)
    {
        var db = shareDb ? SharedDb() : new Db();
        return new EfClipboardHistoryStore(
            db.Factory,
            new FixedCurrentUser(userId),
            Options.Create(opts ?? new ClipboardOptions()));
    }

    private static Db SharedDb()
    {
        lock (SharedLock)
        {
            return _shared ??= new Db();
        }
    }
}
