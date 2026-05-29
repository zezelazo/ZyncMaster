using ZyncMaster.Server.Data;

namespace ZyncMaster.Server;

// Manages the users themselves, so (unlike the other stores) it is NOT scoped to the
// current user. Keyed on the unique (Provider, Subject) pair coming from the identity
// provider.
public interface IUserStore
{
    // Finds the user by (provider, subject); creates it when absent, otherwise refreshes
    // the email / display name. Returns the persisted row.
    Task<UserRow> UpsertAsync(
        string provider, string subject, string email, string displayName, CancellationToken ct = default);

    Task<UserRow?> GetAsync(string id, CancellationToken ct = default);
}
