using System;

namespace ZyncMaster.App.Bridge;

// The identity (sign-in) state surfaced to the web UI via the bridge. Mirrors the server's
// /api/identity/me shape plus a signed-in flag and the access-token expiry the App tracks
// locally to decide when to refresh. When IsSignedIn is false every other field is null.
public sealed record IdentityState(
    bool IsSignedIn,
    string? UserId,
    string? Email,
    string? DisplayName,
    DateTimeOffset? ExpiresAt,
    string? Plan)
{
    // The canonical "nobody is signed in" state. Returned when no token is cached, or after a
    // failed refresh / sign-out.
    public static IdentityState SignedOut { get; } =
        new(IsSignedIn: false, UserId: null, Email: null, DisplayName: null, ExpiresAt: null, Plan: null);
}
