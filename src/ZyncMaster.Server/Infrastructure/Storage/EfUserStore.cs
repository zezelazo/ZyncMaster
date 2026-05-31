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

    // SECURITY: emailVerified trust policy + proof-of-possession enforced at the endpoint
    // layer (see plan v2 §A-4/§B-5). This store is purely mechanical.
    public async Task<UserRow> UpsertByLoginAsync(
        string provider, string providerSubject, string email, bool emailVerified, string displayName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(providerSubject);
        ArgumentNullException.ThrowIfNull(email);

        await using var db = await _factory.CreateDbContextAsync(ct);

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
            return owner;
        }

        // (b) Account-linking: a verified incoming email that matches a verified login on any
        // provider attaches the new login to that existing user.
        UserRow user;
        if (emailVerified)
        {
            var match = await db.IdentityLogins
                .FirstOrDefaultAsync(l => l.Email == email && l.EmailVerified, ct);
            if (match is not null)
            {
                user = await db.Users.FirstAsync(u => u.Id == match.UserId, ct);
                user.DisplayName = displayName;
                user.PrimaryEmail = email;
                db.IdentityLogins.Add(NewLogin(user.Id, provider, providerSubject, email, emailVerified));
                await db.SaveChangesAsync(ct);
                return user;
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
        await db.SaveChangesAsync(ct);
        return user;
    }

    // SECURITY: emailVerified trust policy + proof-of-possession enforced at the endpoint
    // layer (see plan v2 §A-4/§B-5). This store is purely mechanical.
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

        await using var db = await _factory.CreateDbContextAsync(ct);

        var match = await db.IdentityLogins
            .FirstOrDefaultAsync(l => l.Email == email && l.EmailVerified, ct);
        if (match is null)
        {
            return null;
        }

        var user = await db.Users.FirstAsync(u => u.Id == match.UserId, ct);
        user.DisplayName = displayName;
        user.PrimaryEmail = email;
        db.IdentityLogins.Add(NewLogin(user.Id, provider, providerSubject, email, emailVerified));
        await db.SaveChangesAsync(ct);
        return user;
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
