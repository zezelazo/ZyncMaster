namespace ZyncMaster.Server;

// One side of a sync pair. Provider is "OutlookCom" (events pushed from a desktop
// device that reads Outlook Classic via COM) or "MicrosoftGraph" (events read by the
// server directly from a connected account). AccountRef identifies the connected
// account this endpoint belongs to ("default" / null for the single-account case).
public sealed record Endpoint
{
    public required string Provider { get; init; }
    public string? AccountRef { get; init; }
    public required string CalendarId { get; init; }
    public string CalendarName { get; init; } = "";

    // Source-only multi-calendar selection (Feature 2). All three are null/false by default, which
    // means "legacy single calendar" — read exactly CalendarId. They only apply to a pair's SOURCE;
    // the destination is always a single CalendarId and these are ignored on it. Persisted inside
    // SourceJson via PairJson (camelCase, NullValueHandling.Ignore), so legacy rows round-trip
    // unchanged and NO EF schema migration is required.
    //
    //   AllCalendars  — true => read EVERY calendar of the source account (enumerate then read each).
    //   CalendarIds   — Graph: the subset of calendarIds to read (when AllCalendars is false).
    //   CalendarNames — COM display names (device-side only; the server never reads COM, but the field
    //                   round-trips so the device push path and the UI keep their selection).
    public bool AllCalendars { get; init; }
    public IReadOnlyList<string>? CalendarIds { get; init; }
    public IReadOnlyList<string>? CalendarNames { get; init; }
}

// A configured one-way mirror from Source to Destination. State is "active",
// "paused" or "disabled"; disabled pairs are left behind when an account is forgotten.
public sealed record SyncPair
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Endpoint Source { get; init; }
    public required Endpoint Destination { get; init; }
    public int IntervalMin { get; init; }
    public string State { get; init; } = "active";
    public DateTimeOffset? LastRunUtc { get; init; }
    public MirrorResult? LastResult { get; init; }

    // Destinations this pair previously wrote to and must still clean up (FIX 3). When a pair is
    // re-targeted (PATCH changes Destination), the OLD destination still holds the events this
    // pair created (CalImportPairId == pair.Id). The opt-in /cleanup-destination call is the
    // immediate path, but if the client never calls it (crash/close) those events would be
    // orphaned forever. So every old destination is recorded here and DRAINED idempotently at the
    // start of the next run/push: ListManagedByPairAsync already filters by pair.Id, so re-running
    // a partially-completed drain only re-deletes what is still present. Empty by default; the
    // CURRENT destination is never added (it must not be swept). De-duplicated on (provider,
    // accountRef, calendarId).
    public List<Endpoint> PendingCleanupDestinations { get; init; } = new();
}

// Counts from a single mirror run, returned by push/run and stored as a pair's LastResult.
public sealed record MirrorResult
{
    public int Created { get; init; }
    public int Updated { get; init; }
    public int Deleted { get; init; }
    public int Skipped { get; init; }
    public List<string> Failures { get; init; } = new();

    // True when the run did not fully reconcile the window because a transient failure
    // (429 / timeout / network) forced the destructive orphan sweep to be skipped this run
    // (plan v2 §B-2). The applied upserts are durable; orphan cleanup is deferred to a
    // later run. Surfaced so the caller/panel can show "partial — will retry".
    public bool Partial { get; init; }
}

// Summary of a connected account exposed to the panel. AccountRef is the opaque internal id
// (a pool GUID or a legacy UPN) used to address the account on other endpoints; it is NEVER meant
// to be shown to the user. Email is the real mailbox when known (empty for the "default" /
// no-email case) so the UI can show a humane label instead of falling back to the GUID.
public sealed record AccountInfo
{
    public required string AccountRef { get; init; }
    public required string DisplayName { get; init; }
    public string Email { get; init; } = "";
    public bool IsDefault { get; init; }
}
