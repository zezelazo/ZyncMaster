namespace ZyncMaster.Engine;

// Outcome of asking the server to sync a COM-pinned pair now (POST /api/pairs/{id}/request-sync).
// Track B. Status is one of:
//   "requested"          — the server stamped the signal; the pinned device will run it shortly.
//   "origin_unavailable" — the pinned device has no live lease (its App is not running), so the
//                          signal could not be queued for anyone to pick up.
//   "local"             — the CALLER is the pinned device; it should run the pair locally instead.
//   "not_com_pinned"     — surfaced by the client when the server answers 409 for a non-COM pair.
// DeviceName is the pinned device's display name when the server could resolve it, else null.
public sealed record RequestSyncResult
{
    public string Status { get; init; } = "";
    public string? DeviceName { get; init; }
}
