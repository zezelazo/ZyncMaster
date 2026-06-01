using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ZyncMaster.Server.Data;

// The single EF Core context for the server. Persistence-only for WS-A: a fixed
// "default" user is seeded so the existing single-user behavior is identical before
// WS-B adds real users + scoping. Columns are deliberately provider-neutral (string
// keys, DateTimeOffset, JSON-in-text) so a later Npgsql swap is a one-line change.
public sealed class ZyncMasterDbContext : DbContext, IDataProtectionKeyContext
{
    public ZyncMasterDbContext(DbContextOptions<ZyncMasterDbContext> options) : base(options)
    {
    }

    public DbSet<UserRow> Users => Set<UserRow>();
    public DbSet<ConnectedAccountRow> ConnectedAccounts => Set<ConnectedAccountRow>();
    public DbSet<CalendarAccountRow> CalendarAccounts => Set<CalendarAccountRow>();
    public DbSet<DeviceRow> Devices => Set<DeviceRow>();
    public DbSet<PendingPairingRow> PendingPairings => Set<PendingPairingRow>();
    public DbSet<SyncPairRow> SyncPairs => Set<SyncPairRow>();
    public DbSet<SyncRunLockRow> SyncRunLocks => Set<SyncRunLockRow>();
    public DbSet<SyncStateRow> SyncStates => Set<SyncStateRow>();
    public DbSet<IdentityLoginRow> IdentityLogins => Set<IdentityLoginRow>();
    public DbSet<IdentityAccessTokenRow> IdentityAccessTokens => Set<IdentityAccessTokenRow>();
    public DbSet<IdentityRefreshTokenRow> IdentityRefreshTokens => Set<IdentityRefreshTokenRow>();
    public DbSet<MagicLinkRow> MagicLinks => Set<MagicLinkRow>();

    // Data Protection key ring lives in the DB so keys survive restarts and are shared
    // across instances (see AddDataProtection().PersistKeysToDbContext in Program.cs).
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<UserRow>(e =>
        {
            e.ToTable("Users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.Provider).HasMaxLength(64).IsRequired();
            e.Property(x => x.Subject).HasMaxLength(256).IsRequired();
            e.Property(x => x.Email).HasMaxLength(256);
            e.Property(x => x.DisplayName).HasMaxLength(256);
            e.Property(x => x.PrimaryEmail).HasMaxLength(256).IsRequired();
            e.Property(x => x.Plan).HasMaxLength(64);
            e.HasIndex(x => new { x.Provider, x.Subject }).IsUnique();

            // Seed the fixed single-user so the suite keeps single-user behavior pre-WS-B.
            e.HasData(new UserRow
            {
                Id = DefaultCurrentUserAccessor.DefaultUserId,
                Provider = "local",
                Subject = "default",
                Email = null,
                DisplayName = "Default",
                CreatedUtc = DateTimeOffset.UnixEpoch,
                PrimaryEmail = "",
                Plan = null,
            });
        });

        b.Entity<ConnectedAccountRow>(e =>
        {
            e.ToTable("ConnectedAccounts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.UserId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Provider).HasMaxLength(64).IsRequired();
            e.Property(x => x.AccountRef).HasMaxLength(256).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(256);
            e.Property(x => x.EncryptedRefreshToken).IsRequired();
            e.HasIndex(x => new { x.UserId, x.AccountRef }).IsUnique();
        });

        b.Entity<CalendarAccountRow>(e =>
        {
            e.ToTable("CalendarAccounts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.UserId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(32).IsRequired();
            e.Property(x => x.Provider).HasMaxLength(64).IsRequired();
            e.Property(x => x.AccountEmail).HasMaxLength(256).IsRequired();
            e.Property(x => x.Authority).HasMaxLength(512);
            e.Property(x => x.Scope).HasMaxLength(32).IsRequired();
            e.Property(x => x.DeviceId).HasMaxLength(128);
            e.Property(x => x.DisplayName).HasMaxLength(256);
            e.Property(x => x.Status).HasMaxLength(32).IsRequired();
            e.HasIndex(x => x.UserId);
            e.HasOne<UserRow>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DeviceRow>(e =>
        {
            e.ToTable("Devices");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.UserId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.ApiKeyHash).IsRequired();
            e.Property(x => x.TargetCalendarId).HasMaxLength(256);
            e.Property(x => x.KeyId).HasMaxLength(64);
            e.Property(x => x.Platform).HasMaxLength(16).IsRequired().HasDefaultValue("windows");
            e.Property(x => x.HasOutlookCom).HasDefaultValue(false);
            e.Property(x => x.AppVersion).HasMaxLength(64);
            e.HasIndex(x => x.UserId);
            // §A-3 — indexed lookup of the single device matching an incoming key's public keyId.
            // Filtered to non-null so legacy keyless rows do not bloat the index (SQL Server
            // honours the filter; SQLite ignores it harmlessly).
            e.HasIndex(x => x.KeyId).HasFilter("[KeyId] IS NOT NULL");
        });

        b.Entity<PendingPairingRow>(e =>
        {
            e.ToTable("PendingPairings");
            e.HasKey(x => x.PairingId);
            e.Property(x => x.PairingId).HasMaxLength(64);
            e.Property(x => x.UserId).HasMaxLength(64);
            e.Property(x => x.DeviceName).HasMaxLength(256).IsRequired();
            e.Property(x => x.Code).HasMaxLength(64).IsRequired();
            e.Property(x => x.ApprovedDeviceId).HasMaxLength(64);
            e.HasIndex(x => x.Code);
        });

        b.Entity<SyncPairRow>(e =>
        {
            e.ToTable("SyncPairs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.UserId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.SourceJson).IsRequired();
            e.Property(x => x.DestinationJson).IsRequired();
            e.Property(x => x.State).HasMaxLength(32).IsRequired();
            e.HasIndex(x => x.UserId);
        });

        b.Entity<SyncRunLockRow>(e =>
        {
            e.ToTable("SyncRunLocks");
            // One lock row per pair; PairId is the natural primary key so the atomic
            // acquire UPDATE/INSERT contends on a single row.
            e.HasKey(x => x.PairId);
            e.Property(x => x.PairId).HasMaxLength(64);
            e.Property(x => x.Owner).HasMaxLength(128);
            // No FK to SyncPairs: the lock is keyed by the pair identifier the endpoints
            // use and must be acquirable even while the pair row is being read on another
            // connection; a stale lock row is harmless (it just expires).
        });

        b.Entity<SyncStateRow>(e =>
        {
            e.ToTable("SyncStates");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(128);
            e.Property(x => x.UserId).HasMaxLength(64).IsRequired();
            e.Property(x => x.DeviceId).HasMaxLength(64).IsRequired();
            e.HasIndex(x => new { x.UserId, x.DeviceId }).IsUnique();
        });

        b.Entity<IdentityLoginRow>(e =>
        {
            e.ToTable("IdentityLogins");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.UserId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Provider).HasMaxLength(64).IsRequired();
            e.Property(x => x.ProviderSubject).HasMaxLength(256).IsRequired();
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.HasIndex(x => new { x.Provider, x.ProviderSubject }).IsUnique();
            e.HasIndex(x => new { x.Email, x.EmailVerified });
            e.HasOne<UserRow>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<IdentityAccessTokenRow>(e =>
        {
            e.ToTable("IdentityAccessTokens");
            e.HasKey(x => x.Jti);
            e.Property(x => x.Jti).HasMaxLength(64);
            e.Property(x => x.UserId).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.ExpiresAt);
            e.HasOne<UserRow>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<IdentityRefreshTokenRow>(e =>
        {
            e.ToTable("IdentityRefreshTokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.UserId).HasMaxLength(64).IsRequired();
            e.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne<UserRow>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<MagicLinkRow>(e =>
        {
            e.ToTable("MagicLinks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.Property(x => x.Nonce).HasMaxLength(256).IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique();
            // Per-email rate-limit window count + cleanup scans both filter on Email.
            e.HasIndex(x => x.Email);
            // No FK to Users: a magic-link is requested by EMAIL and may resolve to a brand-new
            // user only at callback time, so it deliberately does not reference a UserRow.
        });
    }
}
