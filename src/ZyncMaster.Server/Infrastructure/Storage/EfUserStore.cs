using Microsoft.EntityFrameworkCore;
using ZyncMaster.Server.Data;

namespace ZyncMaster.Server;

// EF-backed user store. Singleton over IDbContextFactory like the other stores, but
// deliberately NOT user-scoped: it manages the user records themselves. Creates a fresh
// DbContext per operation so it can be shared by the singleton composition root.
public sealed class EfUserStore : IUserStore
{
    private readonly IDbContextFactory<ZyncMasterDbContext> _factory;

    public EfUserStore(IDbContextFactory<ZyncMasterDbContext> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public async Task<UserRow> UpsertAsync(
        string provider, string subject, string email, string displayName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(subject);

        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.Users
            .FirstOrDefaultAsync(u => u.Provider == provider && u.Subject == subject, ct);

        if (row is null)
        {
            row = new UserRow
            {
                Id = Guid.NewGuid().ToString("N"),
                Provider = provider,
                Subject = subject,
                Email = email,
                DisplayName = displayName,
                CreatedUtc = DateTimeOffset.UtcNow,
            };
            db.Users.Add(row);
        }
        else
        {
            row.Email = email;
            row.DisplayName = displayName;
        }

        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<UserRow?> GetAsync(string id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
    }
}
