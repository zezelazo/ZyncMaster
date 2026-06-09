using System.Runtime.CompilerServices;

// Exposes internals to the Server test project so pure, account-resolution logic that should
// not be a public API surface (e.g. the §A-2 mailbox-based self-mirror dedupe in PairEndpoints)
// can be unit-tested directly against a fake ILegacyConnectedAccountAdapter, without standing up
// the full cookie-auth + EF-store endpoint pipeline.
[assembly: InternalsVisibleTo("ZyncMaster.Server.Tests")]

// The PostgreSQL integration suite asserts that a real Npgsql PostgresException(23505) wrapped in
// a DbUpdateException is recognised by the internal DeviceService.IsNameConflict classifier.
[assembly: InternalsVisibleTo("ZyncMaster.Server.Postgres.Tests")]
