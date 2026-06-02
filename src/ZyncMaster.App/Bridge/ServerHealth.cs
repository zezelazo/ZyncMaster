namespace ZyncMaster.App.Bridge;

// Result of a single server warm-up / health probe surfaced to the web UI via the bridge.
// The App makes ONE attempt per call (a short timeout); the UI owns the retry/poll loop that
// covers the Azure F1 cold start (the first request after idle can take ~30-60s to wake the
// server). Status values the UI keys off:
//   "ok"          — the server answered the /health probe; continue to the identity gate.
//   "waking"      — the probe timed out or the server returned a transient error. The server is
//                   most likely cold-starting; the UI keeps polling.
//   "unreachable" — a hard network failure (no DNS, refused connection, offline). The UI also
//                   retries this, then shows the friendly error after its budget runs out.
//   "unconfigured"— no server URL is set yet (UnconfiguredEngineActions). The UI skips the gate.
public sealed record ServerHealth(bool Ok, string Status, string? Message)
{
    public static ServerHealth Healthy { get; } = new(true, "ok", null);

    public static ServerHealth Waking(string? message = null) => new(false, "waking", message);

    public static ServerHealth Unreachable(string? message = null) => new(false, "unreachable", message);

    public static ServerHealth Unconfigured { get; } =
        new(false, "unconfigured", "Set the server URL in Settings to start syncing.");
}
