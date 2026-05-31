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
    public DbSet<DeviceRow> Devices => Set<DeviceRow>();
    public DbSet<PendingPairingRow> PendingPairings => Set<PendingPairingRow>();
    public DbSet<SyncPairRow> SyncPairs => Set<SyncPairRow>();
    public DbSet<SyncStateRow> SyncStates => Set<SyncStateRow>();
    public DbSet<IdentityLoginRow> IdentityLogins => Set<IdentityLoginRow>();

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

        b.Entity<DeviceRow>(e =>
        {
            e.ToTable("Devices");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.UserId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.ApiKeyHash).IsRequired();
            e.Property(x => x.TargetCalendarId).HasMaxLength(256);
            e.HasIndex(x => x.UserId);
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
    }
}
