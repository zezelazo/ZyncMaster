using System;
using ZyncMaster.Core;

namespace ZyncMaster.CalImport;

// Thin wrapper over ZyncMaster.Core's OccurrenceId so the per-occurrence upsert key
// is computed in exactly one place. See OccurrenceId for the rationale.
public static class SourceIdFactory
{
    public static string For(string rawId, DateTimeOffset start)
        => OccurrenceId.For(rawId, start);
}
