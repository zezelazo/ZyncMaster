namespace ZyncMaster.Graph;

// Typed classification of a per-item sync failure (plan v2 §B-3). Drives both the
// conditional window sweep (§B-2: a Transient failure means the payload may be
// incomplete, so we must NOT delete anything this run) and the UX the caller surfaces.
public enum SyncErrorKind
{
    // Throttling (429), gateway/server 5xx, request timeout, network/transport drop.
    // The payload we applied may be partial; retrying later is expected to succeed.
    Transient,

    // The user must act before the next run can succeed: expired/withdrawn token,
    // insufficient scope/consent, or the destination calendar no longer exists (404).
    UserRecoverable,

    // A non-retryable programming/config error or an invalid payload. Retrying as-is
    // will keep failing; it needs a code or configuration fix.
    Fatal,
}
