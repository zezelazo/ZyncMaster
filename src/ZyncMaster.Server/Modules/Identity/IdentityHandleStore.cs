namespace ZyncMaster.Server;

// One-time, short-lived (60s) handles that bridge the broker login back to the App over the
// loopback redirect (plan v2 §A-1 / §C-7). The OAuth callback issues a random handle that
// stands in for the real identity access token in the redirect URL; the App immediately
// exchanges the handle for the token. Handles are single-use and expire fast so a leaked
// redirect URL is near-worthless.
//
// SINGLE-INSTANCE ASSUMPTION: this is in-memory, so it only works while the issuing Server
// instance also serves the exchange. Moving to a DB (or sticky sessions) is required before
// scaling out — see plan v2 §C-7. Not done here on purpose: out of scope for Task 2a.
public interface IIdentityHandleStore
{
    // Stores the token behind a fresh random 32-char handle (60s TTL) and returns the handle.
    string IssueHandle(string identityAccessToken);

    // Returns the token for a live handle and DELETES it (one-time); null if unknown/expired.
    string? ConsumeHandle(string handle);
}
