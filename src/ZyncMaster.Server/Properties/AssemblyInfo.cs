using System.Runtime.CompilerServices;

// Exposes internals to the Server test project so pure, account-resolution logic that should
// not be a public API surface (e.g. the §A-2 mailbox-based self-mirror dedupe in PairEndpoints)
// can be unit-tested directly against a fake ILegacyConnectedAccountAdapter, without standing up
// the full cookie-auth + EF-store endpoint pipeline.
[assembly: InternalsVisibleTo("ZyncMaster.Server.Tests")]
