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

            // (a.1) Orphan-repoint migration. Before the email-link branch existed, a Microsoft
            // sign-in was forced to emailVerified:false and minted its OWN empty user, distinct
            // from the magic-link/web user that owns the same verified email (and the pairs /
            // devices). Now that the email is verified, repoint this login at the canonical
            // verified-email user so the session resolves to the account that actually owns the
            // data — instead of stranding it on the empty orphan forever.
            //
            // Idempotent + safe:
            //   * only runs when emailVerified is true (an unverified login is never repointed,
            //     and two different subjects sharing an UNVERIFIED email never merge);
            //   * the target is the SINGLE other user that owns a verified login for this email;
            //     if more than one exists the email is ambiguous and we leave the login where it
            //     is (the conflict is surfaced elsewhere, never silently merged);
            //   * if the login already points at that user it is a no-op (idempotent);
            //   * the vacated orphan user is deleted ONLY when it owns nothing — never when it
            //     still owns pairs/devices/accounts/etc.
            if (emailVerified)
            {
                var target = await ResolveVerifiedEmailTargetAsync(db, email, login.UserId, ct);
                if (target is not null)
                {
                    var orphanId = login.UserId;
                    login.UserId = target.Id;
                    target.DisplayName = displayName;
                    target.PrimaryEmail = email;
                    await db.SaveChangesAsync(ct);
                    await DeleteUserIfOwnsNothingAsync(db, orphanId, ct);
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    return target;
                }
            }

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

    // Resolves the canonical user a login should be repointed to when its email is verified:
    // the SINGLE user — other than the login's current owner — that already owns a verified
    // login with this email. Returns null when there is no such user (nothing to migrate) or
    // when the current owner is itself the (or a) verified-email owner (already canonical).
    // Throws on ambiguity (the same verified email owned by more than one OTHER user), so the
    // store never silently merges an ambiguous identity.
    private static async Task<UserRow?> ResolveVerifiedEmailTargetAsync(
        ZyncMasterDbContext db, string email, string currentOwnerId, CancellationToken ct)
    {
        var otherUserIds = await db.IdentityLogins
            .Where(l => l.Email == email && l.EmailVerified && l.UserId != currentOwnerId)
            .Select(l => l.UserId).Distinct().ToListAsync(ct);

        if (otherUserIds.Count == 0)
            return null;
        if (otherUserIds.Count > 1)
        {
            throw new InvalidOperationException(
                $"Identity conflict: multiple users share verified email '{email}'.");
        }

        return await db.Users.FirstAsync(u => u.Id == otherUserIds[0], ct);
    }

    // Deletes a vacated orphan user ONLY when it owns no domain data. The orphan's own identity
    // rows (its logins/tokens, now detached because the repointed login moved away) are cleaned
    // up alongside it; any row that represents real user-owned data (devices, pairs, calendar
    // accounts, clipboard, sync state, toggles, prefix rules, replica links) blocks the delete so
    // a user with data is NEVER removed. No-op when the id no longer resolves (idempotent).
    private static async Task DeleteUserIfOwnsNothingAsync(
        ZyncMasterDbContext db, string userId, CancellationToken ct)
    {
        var orphan = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (orphan is null)
            return;

        var ownsData =
            await db.Devices.AnyAsync(r => r.UserId == userId, ct) ||
            await db.SyncPairs.AnyAsync(r => r.UserId == userId, ct) ||
            await db.CalendarAccounts.AnyAsync(r => r.UserId == userId, ct) ||
            await db.ConnectedAccounts.AnyAsync(r => r.UserId == userId, ct) ||
            await db.PendingPairings.AnyAsync(r => r.UserId == userId, ct) ||
            await db.SyncStates.AnyAsync(r => r.UserId == userId, ct) ||
            await db.UserToggles.AnyAsync(r => r.UserId == userId, ct) ||
            await db.ClipboardItems.AnyAsync(r => r.UserId == userId, ct) ||
            await db.ClipboardDeviceSettings.AnyAsync(r => r.UserId == userId, ct) ||
            await db.ReplicaLinks.AnyAsync(r => r.UserId == userId, ct) ||
            await db.PrefixRules.AnyAsync(r => r.UserId == userId, ct);

        if (ownsData)
            return;

        // Remove the orphan's leftover identity rows (logins still pointing at it, plus its
        // issued/refresh token ledgers) so no dangling FK remains, then the user itself.
        var staleLogins = await db.IdentityLogins.Where(l => l.UserId == userId).ToListAsync(ct);
        db.IdentityLogins.RemoveRange(staleLogins);
        var accessTokens = await db.IdentityAccessTokens.Where(t => t.UserId == userId).ToListAsync(ct);
        db.IdentityAccessTokens.RemoveRange(accessTokens);
        var refreshTokens = await db.IdentityRefreshTokens.Where(t => t.UserId == userId).ToListAsync(ct);
        db.IdentityRefreshTokens.RemoveRange(refreshTokens);
        db.Users.Remove(orphan);
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

    // GDPR right-to-be-forgotten. Hard-deletes the user and every row scoped to them in ONE
    // transaction, child tables before their parents (FK-safe regardless of which relationships
    // declare a cascade). The DataProtection key ring is global infrastructure, NOT user data, so it
    // is deliberately untouched. Idempotent: a missing user returns false without opening a transaction.
    public async Task<bool> DeleteUserAsync(string userId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(userId);

        await using var db = await _factory.CreateDbContextAsync(ct);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return false;

        // Tables keyed by a parent id (not UserId) — resolve the parents up front so we can delete
        // their children by id.
        var pairIds = await db.SyncPairs.Where(p => p.UserId == userId).Select(p => p.Id).ToListAsync(ct);
        var ruleIds = await db.PrefixRules.Where(r => r.UserId == userId).Select(r => r.Id).ToListAsync(ct);
        var email = user.Email; // magic links are keyed by normalized email, not UserId

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await db.PrefixRuleDestinations.Where(d => ruleIds.Contains(d.RuleId)).ExecuteDeleteAsync(ct);
        await db.PrefixRules.Where(r => r.UserId == userId).ExecuteDeleteAsync(ct);
        await db.ReplicaLinks.Where(r => r.UserId == userId).ExecuteDeleteAsync(ct);
        await db.SyncRunLocks.Where(l => pairIds.Contains(l.PairId)).ExecuteDeleteAsync(ct);
        await db.SyncStates.Where(s => s.UserId == userId).ExecuteDeleteAsync(ct);
        await db.SyncPairs.Where(p => p.UserId == userId).ExecuteDeleteAsync(ct);
        await db.PendingPairings.Where(p => p.UserId == userId).ExecuteDeleteAsync(ct);
        await db.Devices.Where(d => d.UserId == userId).ExecuteDeleteAsync(ct);
        await db.ClipboardItems.Where(c => c.UserId == userId).ExecuteDeleteAsync(ct);
        await db.ClipboardDeviceSettings.Where(c => c.UserId == userId).ExecuteDeleteAsync(ct);
        await db.CalendarAccounts.Where(a => a.UserId == userId).ExecuteDeleteAsync(ct);
        await db.ConnectedAccounts.Where(a => a.UserId == userId).ExecuteDeleteAsync(ct);
        await db.UserToggles.Where(t => t.UserId == userId).ExecuteDeleteAsync(ct);
        await db.IdentityRefreshTokens.Where(t => t.UserId == userId).ExecuteDeleteAsync(ct);
        await db.IdentityAccessTokens.Where(t => t.UserId == userId).ExecuteDeleteAsync(ct);
        await db.IdentityLogins.Where(l => l.UserId == userId).ExecuteDeleteAsync(ct);
        if (!string.IsNullOrEmpty(email))
            await db.MagicLinks.Where(m => m.Email == email).ExecuteDeleteAsync(ct);
        await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(ct);

        await tx.CommitAsync(ct);
        return true;
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
