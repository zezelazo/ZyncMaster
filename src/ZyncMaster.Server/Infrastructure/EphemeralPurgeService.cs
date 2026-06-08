using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZyncMaster.Server.Data;

namespace ZyncMaster.Server;

// §A/§D — ephemeral-table hygiene. A low-frequency background sweep that set-deletes rows which
// have outlived their purpose, keeping the short-lived tables tidy without manual maintenance.
//
// CRITICAL SAFETY RULE (do not relax): the identity token tables (IdentityAccessTokens /
// IdentityRefreshTokens) are revocation ledgers. A row is deleted ONLY when ExpiresAt <= now,
// NEVER by RevokedAt or any other column. A revoked-but-NOT-yet-expired token MUST survive: while
// it could still be presented, its row is what makes ValidateAccessToken / refresh redemption
// reject it. Deleting a revoked-but-live row would silently UN-revoke the token until its natural
// expiry. (This mirrors the "rows may ONLY be deleted when ExpiresAt <= now" note on
// IdentityAccessTokenRow.) Magic-links and run-locks have no such ledger semantics, so they may be
// purged on broader conditions (consumed link / passed lock).
//
// Deletes are set-based (ExecuteDeleteAsync) over a fresh DbContext per sweep from the singleton
// IDbContextFactory, so the job holds no scoped state and never contends with request handlers.
//
// Test isolation: the BackgroundService timer loop is registered in Program.cs ONLY outside the
// Development environment, so the WebApplicationFactory test host (Development) never starts the
// timer and `dotnet test` cannot hang on it. The purge LOGIC stays fully testable through the
// public PurgeOnceAsync(now) entry point, which the unit tests drive directly with a fixed clock.
public sealed class EphemeralPurgeService : BackgroundService
{
    private readonly IDbContextFactory<ZyncMasterDbContext> _factory;
    private readonly ILogger<EphemeralPurgeService> _logger;
    private readonly TimeSpan _interval;
    private readonly int _pendingPairingTtlMinutes;

    public EphemeralPurgeService(
        IDbContextFactory<ZyncMasterDbContext> factory,
        ILogger<EphemeralPurgeService> logger,
        IOptions<ServerOptions>? options = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var hours = options?.Value.EphemeralPurgeIntervalHours ?? 6;
        _interval = TimeSpan.FromHours(hours <= 0 ? 6 : hours);
        var ttl = options?.Value.PendingPairingTtlMinutes ?? 15;
        _pendingPairingTtlMinutes = ttl <= 0 ? 15 : ttl;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        do
        {
            try
            {
                var purged = await PurgeOnceAsync(DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
                if (purged > 0)
                    _logger.LogInformation("Ephemeral purge removed {Rows} expired/consumed rows.", purged);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Best-effort: a transient DB failure must not crash the host. The next tick retries.
                _logger.LogWarning(ex, "Ephemeral purge sweep failed; will retry next interval.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    // Runs exactly one purge pass against `now`. Returns the total number of rows deleted across
    // all five ephemeral tables. Public + parameterised on `now` so the deletion policy is
    // unit-testable with no timer and a deterministic clock. See the class doc for the token-table
    // safety rule.
    //
    // Deletes are issued as parameterised, set-based SQL (ExecuteSqlInterpolatedAsync) rather than
    // a LINQ ExecuteDeleteAsync: the predicates compare a DateTimeOffset column to `now`, and that
    // comparison is NOT translatable by every provider through LINQ (SQLite throws on a
    // DateTimeOffset relational compare — the same reason CronSyncRunner / the magic-link cleanup
    // evaluate their DateTimeOffset filters in memory). Raw parameterised SQL is translated by both
    // PostgreSQL (prod) and SQLite (tests); identifiers double-quoted and booleans as TRUE/FALSE so
    // both providers parse them identically (unquoted identifiers fold to lowercase on PostgreSQL,
    // never matching the quoted PascalCase tables). It keeps the delete set-based (no row
    // materialising) and is injection-safe via the interpolation parameters. Table/column names
    // mirror the ZyncMasterDbContext ToTable/property mapping.
    public async Task<int> PurgeOnceAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Identity tokens: ExpiresAt <= now ONLY. Never branch on RevokedAt — see class doc.
        var access = await db.Database.ExecuteSqlInterpolatedAsync(
            $@"DELETE FROM ""IdentityAccessTokens"" WHERE ""ExpiresAt"" <= {now}", ct).ConfigureAwait(false);

        var refresh = await db.Database.ExecuteSqlInterpolatedAsync(
            $@"DELETE FROM ""IdentityRefreshTokens"" WHERE ""ExpiresAt"" <= {now}", ct).ConfigureAwait(false);

        // Magic-links: expired OR already consumed (single-use; a consumed link is dead weight).
        var magic = await db.Database.ExecuteSqlInterpolatedAsync(
            $@"DELETE FROM ""MagicLinks"" WHERE ""ExpiresAt"" <= {now} OR ""ConsumedAt"" IS NOT NULL", ct).ConfigureAwait(false);

        // Run-locks: a lock whose LockedUntil has passed is free; the row is no longer meaningful.
        var locks = await db.Database.ExecuteSqlInterpolatedAsync(
            $@"DELETE FROM ""SyncRunLocks"" WHERE ""LockedUntil"" <= {now}", ct).ConfigureAwait(false);

        // Pending pairings (FIX A): a pairing whose CreatedUtc + TTL has passed is dead — its code can
        // no longer be viewed at /pair, approved, or completed — so the row is swept here. Previously
        // PendingPairings was NEVER purged: an approved-but-uncompleted (or never-approved) row lived
        // forever, leaving stale codes and (briefly) a live OneTimeApiKey in the table. The cutoff is
        // now - PendingPairingTtlMinutes; rows older than that are removed regardless of Approved.
        var pairingCutoff = now.AddMinutes(-_pendingPairingTtlMinutes);
        var pairings = await db.Database.ExecuteSqlInterpolatedAsync(
            $@"DELETE FROM ""PendingPairings"" WHERE ""CreatedUtc"" < {pairingCutoff}", ct).ConfigureAwait(false);

        return access + refresh + magic + locks + pairings;
    }
}
