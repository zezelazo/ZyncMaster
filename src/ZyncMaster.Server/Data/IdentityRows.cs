namespace ZyncMaster.Server.Data;

// A single external-identity login (one provider's view of a user). Multiple logins can
// point at the same canonical UserRow when they have been linked (same verified email).
// Kept in a separate file so the new identity schema doesn't disturb Entities.cs / the
// existing migrations history.
public sealed class IdentityLoginRow
{
    public string Id { get; set; } = "";

    // FK -> Users.Id (the canonical user this login resolves to).
    public string UserId { get; set; } = "";

    // "local" | "microsoft" | "google" | "facebook".
    public string Provider { get; set; } = "";

    // The provider's stable subject identifier for the user (oid / sub / etc.).
    public string ProviderSubject { get; set; } = "";

    public string Email { get; set; } = "";

    public bool EmailVerified { get; set; }

    public DateTimeOffset LinkedAt { get; set; }
}
