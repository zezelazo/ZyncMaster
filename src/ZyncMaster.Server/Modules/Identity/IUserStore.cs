using ZyncMaster.Server.Data;

namespace ZyncMaster.Server;

// Manages the users themselves, so (unlike the other stores) it is NOT scoped to the
// current user. Keyed on the unique (Provider, Subject) pair coming from the identity
// provider.
public interface IUserStore
{
    // Finds the user by (provider, subject); creates it when absent, otherwise refreshes
    // the email / display name. Returns the persisted row. Legacy path used by the existing
    // /connect flow — kept intact.
    Task<UserRow> UpsertAsync(
        string provider, string subject, string email, string displayName, CancellationToken ct = default);

    // Multi-provider identity upsert keyed on the IdentityLogins table:
    //   (a) if an IdentityLoginRow with (provider, providerSubject) exists, refresh its
    //       email/displayName (and the user's) and return that user;
    //   (b) else, when emailVerified, link the new login to any user that already owns a
    //       verified login with the same email (account linking);
    //   (c) else create a brand-new user + login.
    // SECURITY: emailVerified trust policy + proof-of-possession enforced at the endpoint
    // layer (see plan v2 §A-4/§B-5). This store is purely mechanical.
    Task<UserRow> UpsertByLoginAsync(
        string provider, string providerSubject, string email, bool emailVerified, string displayName,
        CancellationToken ct = default);

    // Links a second login to an existing user ONLY when emailVerified and a verified login
    // with the same email already exists. Returns that user, or null when no link is made.
    // SECURITY: emailVerified trust policy + proof-of-possession enforced at the endpoint
    // layer (see plan v2 §A-4/§B-5).
    Task<UserRow?> TryLinkByEmailAsync(
        string provider, string providerSubject, string email, bool emailVerified, string displayName,
        CancellationToken ct = default);

    Task<UserRow?> GetAsync(string id, CancellationToken ct = default);

    // GDPR right-to-be-forgotten: hard-deletes the user and EVERY row scoped to them (accounts,
    // devices, pairs, sync state, clipboard, replica/prefix rules, identity logins + tokens, magic
    // links) in one transaction. Idempotent: returns false when the user does not exist.
    Task<bool> DeleteUserAsync(string userId, CancellationToken ct = default);
}
