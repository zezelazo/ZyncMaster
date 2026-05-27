using System;
using SyncMaster.Core;

namespace SyncMaster.CalImport;

// Thin wrapper over SyncMaster.Core's OccurrenceId so the per-occurrence upsert key
// is computed in exactly one place. See OccurrenceId for the rationale.
public static class SourceIdFactory
{
    public static string For(string rawId, DateTimeOffset start)
        => OccurrenceId.For(rawId, start);
}
