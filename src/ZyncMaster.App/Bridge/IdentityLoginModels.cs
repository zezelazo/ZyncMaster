namespace ZyncMaster.App.Bridge;

// The result of a sign-in attempt (Microsoft broker or magic-link callback). On success the
// resolved IdentityState is included so the UI can render the signed-in user immediately; on
// failure Error carries a human-readable reason (timeout, nonce mismatch, handle error, …).
public sealed record LoginOutcome(bool Success, IdentityState? State, string? Error)
{
    public static LoginOutcome Ok(IdentityState state) => new(true, state, null);
    public static LoginOutcome Fail(string error) => new(false, null, error);
}

// The result of kicking off a magic-link sign-in. Requested is true once the Server has accepted
// the request (constant 202) and the App is waiting for the user to click the emailed link; the
// actual sign-in completes later on the loopback callback. Error carries a reason when the
// request could not be started.
public sealed record RequestMagicLinkOutcome(bool Requested, string? Error)
{
    public static RequestMagicLinkOutcome Ok() => new(true, null);
    public static RequestMagicLinkOutcome Fail(string error) => new(false, error);
}
