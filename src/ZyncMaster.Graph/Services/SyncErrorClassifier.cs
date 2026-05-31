using System;
using System.Net;
using System.Net.Http;

namespace ZyncMaster.Graph;

// Maps an exception thrown while applying one item of a mirror run to a typed
// SyncErrorKind (plan v2 §B-3). Pure and deterministic so it is fully unit-testable and
// callable from CalendarMirror's per-item catch.
//
//   Transient        429 / 503 / 500 / 502 / 504 / request timeout / network drop.
//   UserRecoverable  token expired / 401 / insufficient_scope / destination 404.
//   Fatal            invalid argument / invalid payload / anything else non-retryable.
public static class SyncErrorClassifier
{
    public static SyncErrorKind Classify(Exception ex)
    {
        if (ex == null) throw new ArgumentNullException(nameof(ex));

        switch (ex)
        {
            // Token acquisition / 401-after-refresh: the user must re-consent or reconnect.
            case AuthenticationFailedException:
                return SyncErrorKind.UserRecoverable;

            // The retry budget in GraphCalendarTarget is already exhausted by the time this
            // surfaces, but the underlying cause is still transient: throttling, a 5xx, a
            // request timeout or a transport drop. We must NOT sweep on these.
            case GraphRequestException gre:
                return ClassifyGraphRequest(gre);

            // Raw transport/timeout that escaped without being wrapped (defensive).
            case HttpRequestException:
            case TaskCanceledException:
            case TimeoutException:
                return SyncErrorKind.Transient;

            // Bad arguments / contract violations from our own code or a malformed payload.
            case ArgumentException:
            case InvalidOperationException:
            case FormatException:
                return SyncErrorKind.Fatal;

            // Unknown exception type: Fatal BY DESIGN. Fatal does not itself block the sweep
            // (only Transient does), so an unknown error must NOT be a hidden way to keep the
            // calendar safe — every TRULY retryable failure has to arrive typed (a
            // GraphRequestException with IsTransient = true, or one of the raw transient cases
            // above). If a new retryable failure mode is added, mark it transient at the throw
            // site; do not lean on this default.
            default:
                return SyncErrorKind.Fatal;
        }
    }

    private static SyncErrorKind ClassifyGraphRequest(GraphRequestException gre)
    {
        // Typed signal first. Every retryable throw in GraphCalendarTarget /
        // MicrosoftGraphProvider (throttling, 5xx, request timeout, transport drop, and the
        // malformed/truncated-read errors from the pagination hardening) sets IsTransient.
        // Keying off the flag — not the message wording — means renaming a message string can
        // never silently re-open the data-loss path (a transient misread as non-transient
        // would let the destructive sweep run on an incomplete source set).
        if (gre.IsTransient)
            return SyncErrorKind.Transient;

        var msg = gre.Message ?? "";

        // Legacy message-marker fallback, kept only for safety in case some future throw
        // forgets to set the typed flag. New transient throws MUST set IsTransient = true;
        // do not rely on this wording match.
        if (Contains(msg, "transient")
            || Contains(msg, "timed out")
            || Contains(msg, "transport error"))
            return SyncErrorKind.Transient;

        // Status-code driven classification for "Graph request failed: <code> ...".
        if (TryExtractStatus(msg, out var status))
        {
            if (status == 429 || status == 500 || status == 502 || status == 503 || status == 504
                || status == (int)HttpStatusCode.RequestTimeout) // 408
                return SyncErrorKind.Transient;

            if (status == (int)HttpStatusCode.Unauthorized          // 401
                || status == (int)HttpStatusCode.Forbidden          // 403 (insufficient scope)
                || status == (int)HttpStatusCode.NotFound)          // 404 destination missing
                return SyncErrorKind.UserRecoverable;
        }

        // Message-marker fallbacks for the auth-shaped failures Graph words in the body.
        if (Contains(msg, "insufficient_scope")
            || Contains(msg, "invalidauthenticationtoken")
            || Contains(msg, "token is expired")
            || Contains(msg, "expired token"))
            return SyncErrorKind.UserRecoverable;

        // A GraphRequestException we cannot place (e.g. malformed response, missing id) is
        // not safe to call transient — treat as Fatal so we surface it rather than silently
        // retry forever, but it still does NOT block the sweep.
        return SyncErrorKind.Fatal;
    }

    // Parses the integer that follows "failed: " in messages shaped like
    // "Graph request failed: 404 Not Found. ...". Returns false when no code is present.
    private static bool TryExtractStatus(string msg, out int status)
    {
        status = 0;
        const string marker = "failed: ";
        var i = msg.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return false;

        var start = i + marker.Length;
        var j = start;
        while (j < msg.Length && char.IsDigit(msg[j])) j++;
        if (j == start) return false;

        return int.TryParse(msg.AsSpan(start, j - start), out status);
    }

    private static bool Contains(string haystack, string needle)
        => haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
}
