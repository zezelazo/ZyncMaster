using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ZyncMaster.App.Bridge;

// The query parameters delivered to the loopback when the Server redirects the browser back to
// http://127.0.0.1:{port}/identity/callback?handle=...&nonce=... after a successful sign-in.
public sealed record LoopbackCallback(IReadOnlyDictionary<string, string> Query);

// A one-shot loopback listener for the identity sign-in flow: bind an OS-assigned ephemeral port
// on 127.0.0.1, hand the port to the caller (so it can be embedded in the OAuth state), then
// await the single browser callback. Mirrors PairingService's loopback pattern; the concrete
// HttpListener implementation is untested infrastructure (CLAUDE.md), so the orchestration above
// it is tested against a fake of this interface.
public interface IIdentityLoopback
{
    // The 127.0.0.1 port the listener is bound to. Valid after StartAsync returns.
    int Port { get; }

    // Begins listening on an ephemeral loopback port at /identity/callback/.
    Task StartAsync(CancellationToken ct = default);

    // Awaits the single callback request, returning its query parameters. Honours the cancellation
    // token (the caller wires a timeout) — a cancelled wait surfaces as OperationCanceledException.
    Task<LoopbackCallback> WaitForCallbackAsync(CancellationToken ct = default);

    // Stops the listener and releases the port. Safe to call more than once.
    void Stop();
}

// Opens a URL in the user's default system browser. The App already has IBrowserLauncher in the
// Engine for pairing; this alias keeps the identity flow's dependency explicit and lets tests
// substitute a fake without pulling the Engine launcher in.
public interface ISystemBrowser
{
    void Open(string url);
}
