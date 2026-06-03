namespace ZyncMaster.App.Bridge;

// The result of a one-click "connect a calendar account" attempt. On success Connected is true and
// the UI refreshes its account list (the account is now persisted server-side under the signed-in
// user). On failure Error carries a human-readable reason (not signed in, timeout, nonce mismatch,
// server rejection). Cancelled is true when the user aborted the attempt or it was superseded — the
// UI treats that as a quiet "back to the button", not an error banner. Mirrors LoginOutcome.
public sealed record ConnectCalendarOutcome(bool Connected, string? Error, bool Cancelled = false)
{
    public static ConnectCalendarOutcome Ok() => new(true, null);
    public static ConnectCalendarOutcome Fail(string error) => new(false, error);
    public static ConnectCalendarOutcome Canceled() => new(false, null, true);
}
