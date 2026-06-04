namespace ZyncMaster.Server;

// Best-effort lookup of a connected Microsoft account's real mailbox + display name via the
// Graph /me endpoint. Used to capture the account email when the calendar token-exchange did not
// return an id_token (the calendar scopes intentionally omit `openid`, so no identity claims come
// back) and to backfill accounts that were connected before this capture existed.
//
// Implementations MUST be best-effort: any transport/HTTP failure returns an empty result rather
// than throwing, so a failed /me never breaks the connect callback or the account listing.
public interface IGraphUserInfoService
{
    // Calls GET https://graph.microsoft.com/v1.0/me?$select=mail,userPrincipalName,displayName
    // with the supplied access token (the User.Read scope is granted for both read and
    // read/write calendar connections, so /me succeeds). Returns the resolved email
    // (mail ?? userPrincipalName) and displayName, or an all-empty result on any failure.
    Task<GraphUserInfo> GetMeAsync(string accessToken, CancellationToken ct = default);
}

// The subset of /me we care about. Email is `mail` when the mailbox has one, else
// userPrincipalName; both can be empty when /me failed or returned nothing usable.
public readonly record struct GraphUserInfo(string Email, string DisplayName)
{
    public static readonly GraphUserInfo Empty = new("", "");

    // True when /me produced at least an email we can show instead of the internal accountRef.
    public bool HasEmail => !string.IsNullOrWhiteSpace(Email);
}
