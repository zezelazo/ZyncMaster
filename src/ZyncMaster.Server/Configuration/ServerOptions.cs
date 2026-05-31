namespace ZyncMaster.Server;

public sealed class ServerOptions
{
    public string MicrosoftClientId { get; set; } = "";
    public string Authority { get; set; } = "https://login.microsoftonline.com/common/oauth2/v2.0";
    public string RedirectUri { get; set; } = "";
    public string Scopes { get; set; } = "offline_access Calendars.ReadWrite User.Read";
    public int SyncWindowDays { get; set; } = 14;
    public string ExtendedPropertyGuid { get; set; } = "6f0e7f2c-3b1a-4e8d-9c2f-7a5b1d9e4c30";

    // Identity access token (internal Server<->App bearer) lifetime. Short by design: the
    // token is a master key that registers devices and connects calendars, so it is renewed
    // via the long-lived refresh token rather than being long-lived itself (plan v2 §A-1).
    public int IdentityAccessTokenTtlMinutes { get; set; } = 1440; // 24h

    // Identity refresh token lifetime. Long-lived, opaque, stored hashed; exchanged for a
    // fresh access token at App start / on near-expiry.
    public int IdentityRefreshTokenTtlDays { get; set; } = 90;
}
