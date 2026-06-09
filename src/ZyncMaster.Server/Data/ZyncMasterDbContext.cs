using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

    // Keep DeviceRow.NameLower (the uniqueness key behind the unique (UserId, NameLower) index) in
    // lock-step with Name on every write, no matter who writes the row — the store, a seeder, or a
    // test that adds a DeviceRow directly. Deriving it here means the column can never drift out of
    // sync with the user-visible Name and callers never have to remember to set it.
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        SyncDeviceNameLower();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        SyncDeviceNameLower();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
    }

    private void SyncDeviceNameLower()
    {
        foreach (var entry in ChangeTracker.Entries<DeviceRow>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
                entry.Entity.NameLower = (entry.Entity.Name ?? string.Empty).Trim().ToLowerInvariant();
        }
    }

    public DbSet<UserRow> Users => Set<UserRow>();
    public DbSet<ConnectedAccountRow> ConnectedAccounts => Set<ConnectedAccountRow>();
    public DbSet<CalendarAccountRow> CalendarAccounts => Set<CalendarAccountRow>();
    public DbSet<DeviceRow> Devices => Set<DeviceRow>();
    public DbSet<PendingPairingRow> PendingPairings => Set<PendingPairingRow>();
    public DbSet<SyncPairRow> SyncPairs => Set<SyncPairRow>();
    public DbSet<SyncRunLockRow> SyncRunLocks => Set<SyncRunLockRow>();
    public DbSet<SyncStateRow> SyncStates => Set<SyncStateRow>();
    public DbSet<UserToggleRow> UserToggles => Set<UserToggleRow>();
    public DbSet<IdentityLoginRow> IdentityLogins => Set<IdentityLoginRow>();
    public DbSet<IdentityAccessTokenRow> IdentityAccessTokens => Set<IdentityAccessTokenRow>();
    public DbSet<IdentityRefreshTokenRow> IdentityRefreshTokens => Set<IdentityRefreshTokenRow>();
    public DbSet<MagicLinkRow> MagicLinks => Set<MagicLinkRow>();
    public DbSet<ClipboardItemRow> ClipboardItems => Set<ClipboardItemRow>();
    public DbSet<ClipboardDeviceSettingsRow> ClipboardDeviceSettings => Set<ClipboardDeviceSettingsRow>();

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
            // Derived lowercase uniqueness key. Capped to match Name; never null (stores write
            // the lowercased Name).
            e.Property(x => x.NameLower).HasMaxLength(256).IsRequired();
            e.Property(x => x.ApiKeyHash).IsRequired();
            e.Property(x => x.TargetCalendarId).HasMaxLength(256);
            e.Property(x => x.KeyId).HasMaxLength(64);
            e.Property(x => x.Platform).HasMaxLength(16).IsRequired().HasDefaultValue("windows");
            e.Property(x => x.HasOutlookCom).HasDefaultValue(false);
            e.Property(x => x.AppVersion).HasMaxLength(64);
            e.HasIndex(x => x.UserId);
            // §A-3 — indexed lookup of the single device matching an incoming key's public keyId.
            // Filtered to non-null so legacy keyless rows do not bloat the index. The identifier is
            // double-quoted (standard SQL) so the partial-index predicate parses identically on
            // PostgreSQL (prod) and SQLite (tests); SQL Server bracket syntax ([KeyId]) is a syntax
            // error on PostgreSQL.
            e.HasIndex(x => x.KeyId).HasFilter("\"KeyId\" IS NOT NULL");
            // Per-user device-name uniqueness, case-insensitive on BOTH providers via the derived
            // NameLower column. EnsureCreated (SQLite tests) and Migrate (SQL Server prod) both pick
            // this up; the matching migration applies it to existing prod databases.
            e.HasIndex(x => new { x.UserId, x.NameLower }).IsUnique();
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
            // FIX 1 — SHA-256 base64url is 43 chars; 128 leaves ample headroom and stays nullable.
            e.Property(x => x.VerifierHash).HasMaxLength(128);
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
            // COM device-pinning (Track B). Nullable column capped to the device-id length; indexed
            // so each device's scheduler can cheaply select the pairs pinned to it.
            e.Property(x => x.PinnedDeviceId).HasMaxLength(64);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.PinnedDeviceId);
        });

        b.Entity<SyncRunLockRow>(e =>
        {
            e.ToTable("SyncRunLocks");
            // One lock row per pair; PairId is the natural primary key so the atomic
            // acquire UPDATE/INSERT contends on a single row.
            e.HasKey(x => x.PairId);
            e.Property(x => x.PairId).HasMaxLength(64);
            e.Property(x => x.Owner).HasMaxLength(128);
            e.Property(x => x.FenceToken).HasMaxLength(64);
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

        b.Entity<UserToggleRow>(e =>
        {
            e.ToTable("UserToggles");
            // One row per user; UserId is the natural primary key.
            e.HasKey(x => x.UserId);
            e.Property(x => x.UserId).HasMaxLength(64);
            e.Property(x => x.CloudFallbackSync).HasDefaultValue(true);
            e.HasOne<UserRow>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
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

        b.Entity<ClipboardItemRow>(e =>
        {
            e.ToTable("ClipboardItems");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.UserId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Type).HasMaxLength(16).IsRequired();
            e.Property(x => x.OriginDeviceId).HasMaxLength(64).IsRequired();
            e.Property(x => x.OriginDeviceName).HasMaxLength(256);
            // List newest-first per user: the history query filters by UserId and orders by CreatedUtc.
            e.HasIndex(x => new { x.UserId, x.CreatedUtc });
        });

        b.Entity<ClipboardDeviceSettingsRow>(e =>
        {
            e.ToTable("ClipboardDeviceSettings");
            // One row per device; DeviceId is the natural primary key.
            e.HasKey(x => x.DeviceId);
            e.Property(x => x.DeviceId).HasMaxLength(64);
            e.Property(x => x.UserId).HasMaxLength(64).IsRequired();
            e.Property(x => x.ViewerHotkey).HasMaxLength(64).IsRequired();
            e.Property(x => x.Density).HasMaxLength(16).IsRequired();
            // SPKI public key, base64: ~400 chars for RSA-2048; 4096 leaves headroom for
            // larger key sizes without going unbounded.
            e.Property(x => x.PublicKeyBase64).HasMaxLength(4096);
            e.HasIndex(x => x.UserId);
        });
    }
}
