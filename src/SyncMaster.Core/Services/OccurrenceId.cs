using System;
using System.Globalization;

namespace SyncMaster.Core;

// Computes the stable per-occurrence upsert key for an event.
//
// Outlook shares a single GlobalAppointmentID across every occurrence of a
// recurring series, so the raw id from the export is NOT unique per occurrence.
// Combining it with the occurrence start (UTC) yields a unique, deterministic id:
// the same occurrence always maps to the same key, which keeps downstream upserts
// idempotent across re-imports and re-exports, while distinct occurrences of the
// same series map to distinct events in the target calendar.
public static class OccurrenceId
{
    // Fixed project constant — never change it or every existing event's id drifts.
    private static readonly Guid Namespace = new Guid("9b6f1d2e-4c3a-5e7b-8f1d-2a3c4e5b6d7f");

    public static string For(string rawId, DateTimeOffset start)
    {
        if (rawId == null) throw new ArgumentNullException(nameof(rawId));

        var seed = rawId + "|" + start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        return UuidV5.Create(Namespace, seed).ToString("D");
    }
}
