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
                PrimaryEmail = email,
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

    // SECURITY: emailVerified trust policy + proof-of-possession enforced at the endpoint
    // layer (see plan v2 §A-4/§B-5). This store is purely mechanical: it only DETECTS an
    // identity conflict (a verified email shared by more than one canonical user) and stops.
    // The POLICY for resolving/merging such conflicts is decided at the endpoint layer.
    public async Task<UserRow> UpsertByLoginAsync(
        string provider, string providerSubject, string email, bool emailVerified, string displayName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(providerSubject);
        ArgumentNullException.ThrowIfNull(email);

        // Normalize provider/email for storage and lookup. providerSubject is left untouched:
        // the IdP defines its subject as case-sensitive.
        provider = provider.ToLowerInvariant();
        email = email.Trim().ToLowerInvariant();

        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // (a) Existing login for this (provider, providerSubject): refresh and return.
        var login = await db.IdentityLogins
            .FirstOrDefaultAsync(l => l.Provider == provider && l.ProviderSubject == providerSubject, ct);
        if (login is not null)
        {
            login.Email = email;
            login.EmailVerified = emailVerified;
            var owner = await db.Users.FirstAsync(u => u.Id == login.UserId, ct);
            owner.DisplayName = displayName;
            owner.PrimaryEmail = email;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return owner;
        }

        // (b) Account-linking: a verified incoming email that matches a verified login on any
        // provider attaches the new login to that existing user.
        UserRow user;
        if (emailVerified)
        {
            // Detect identity conflicts: a verified email pointing at more than one canonical
            // user is ambiguous and must not be auto-linked. The store only detects and cuts.
            var userIds = await db.IdentityLogins
                .Where(l => l.Email == email && l.EmailVerified)
                .Select(l => l.UserId).Distinct().ToListAsync(ct);
            if (userIds.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Identity conflict: multiple users share verified email '{email}'.");
            }
            if (userIds.Count == 1)
            {
                user = await db.Users.FirstAsync(u => u.Id == userIds[0], ct);
                user.DisplayName = displayName;
                user.PrimaryEmail = email;
                db.IdentityLogins.Add(NewLogin(user.Id, provider, providerSubject, email, emailVerified));
                return await CommitOrReconcileAsync(db, tx, provider, providerSubject, ct);
            }
        }

        // (c) Brand-new user + login.
        user = new UserRow
        {
            Id = Guid.NewGuid().ToString("N"),
            Provider = provider,
            Subject = providerSubject,
            Email = email,
            DisplayName = displayName,
            CreatedUtc = DateTimeOffset.UtcNow,
            PrimaryEmail = email,
            Plan = null,
        };
        db.Users.Add(user);
        db.IdentityLogins.Add(NewLogin(user.Id, provider, providerSubject, email, emailVerified));
        return await CommitOrReconcileAsync(db, tx, provider, providerSubject, ct);
    }

    // SECURITY: emailVerified trust policy + proof-of-possession enforced at the endpoint
    // layer (see plan v2 §A-4/§B-5). This store is purely mechanical: it only DETECTS an
    // identity conflict (a verified email shared by more than one canonical user) and stops.
    // The POLICY for resolving/merging such conflicts is decided at the endpoint layer.
    public async Task<UserRow?> TryLinkByEmailAsync(
        string provider, string providerSubject, string email, bool emailVerified, string displayName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(providerSubject);
        ArgumentNullException.ThrowIfNull(email);

        if (!emailVerified)
        {
            return null;
        }

        // Normalize provider/email; providerSubject stays case-sensitive (see UpsertByLoginAsync).
        provider = provider.ToLowerInvariant();
        email = email.Trim().ToLowerInvariant();

        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Detect identity conflicts: a verified email shared by more than one canonical user is
        // ambiguous and must not be auto-linked. The store only detects and cuts.
        var userIds = await db.IdentityLogins
            .Where(l => l.Email == email && l.EmailVerified)
            .Select(l => l.UserId).Distinct().ToListAsync(ct);
        if (userIds.Count > 1)
        {
            throw new InvalidOperationException(
                $"Identity conflict: multiple users share verified email '{email}'.");
        }
        if (userIds.Count == 0)
        {
            return null;
        }

        var user = await db.Users.FirstAsync(u => u.Id == userIds[0], ct);
        user.DisplayName = displayName;
        user.PrimaryEmail = email;
        db.IdentityLogins.Add(NewLogin(user.Id, provider, providerSubject, email, emailVerified));
        return await CommitOrReconcileAsync(db, tx, provider, providerSubject, ct);
    }

    // Commits the linking/creation work atomically. If a concurrent insert won the race on the
    // unique (Provider, ProviderSubject) index, the constraint violation is swallowed and the
    // already-persisted login's owner is returned instead of bubbling a 500.
    private async Task<UserRow> CommitOrReconcileAsync(
        ZyncMasterDbContext db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx,
        string provider, string providerSubject, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (IsProviderSubjectConflict(ex))
        {
            await tx.RollbackAsync(ct);
            await using var reread = await _factory.CreateDbContextAsync(ct);
            var existing = await reread.IdentityLogins
                .FirstAsync(l => l.Provider == provider && l.ProviderSubject == providerSubject, ct);
            return await reread.Users.FirstAsync(u => u.Id == existing.UserId, ct);
        }

        var login = await db.IdentityLogins
            .FirstAsync(l => l.Provider == provider && l.ProviderSubject == providerSubject, ct);
        return await db.Users.FirstAsync(u => u.Id == login.UserId, ct);
    }

    // True when the failure is a unique-constraint violation on the (Provider, ProviderSubject)
    // index. Matched on the column names so it works on both SQLite (tests) and SQL Server.
    private static bool IsProviderSubjectConflict(DbUpdateException ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("Provider", StringComparison.OrdinalIgnoreCase)
            && message.Contains("ProviderSubject", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<UserRow?> GetAsync(string id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
    }

    private static IdentityLoginRow NewLogin(
        string userId, string provider, string providerSubject, string email, bool emailVerified) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        UserId = userId,
        Provider = provider,
        ProviderSubject = providerSubject,
        Email = email,
        EmailVerified = emailVerified,
        LinkedAt = DateTimeOffset.UtcNow,
    };
}
