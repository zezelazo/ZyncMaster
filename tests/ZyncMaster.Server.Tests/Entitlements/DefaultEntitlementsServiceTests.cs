using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using ZyncMaster.Server.Data;
using ZyncMaster.Server.Tests.Storage;
using Xunit;

namespace ZyncMaster.Server.Tests.Entitlements;

// Unit coverage for the entitlements resolution: defaults (everything unlocked) intersected with
// the user's UserToggleRow. Backed by the SQLite-in-memory harness (no HTTP host).
public sealed class DefaultEntitlementsServiceTests
{
    [Fact]
    public async Task No_toggle_row_returns_everything_unlocked()
    {
        using var harness = new EfStoreTestHarness();
        var service = new DefaultEntitlementsService(harness.Factory);

        var ent = await service.GetForUserAsync("default", CancellationToken.None);

        ent.CloudFallbackSync.Should().BeTrue();
        ent.MaxPairs.Should().Be(int.MaxValue);
        ent.MaxConnectedAccounts.Should().Be(int.MaxValue);
        ent.MinSyncIntervalMinutes.Should().Be(1);
        ent.EnabledModules.Should().BeEquivalentTo(new[] { "calendar", "files", "clipboard" });
    }

    [Fact]
    public async Task Toggle_off_only_flips_cloud_fallback_rest_stays_unlocked()
    {
        using var harness = new EfStoreTestHarness();
        await using (var db = harness.NewContext())
        {
            db.UserToggles.Add(new UserToggleRow { UserId = "default", CloudFallbackSync = false });
            await db.SaveChangesAsync();
        }

        var service = new DefaultEntitlementsService(harness.Factory);
        var ent = await service.GetForUserAsync("default", CancellationToken.None);

        ent.CloudFallbackSync.Should().BeFalse();
        // Everything else is still unlocked — the toggle only gates cloud fallback.
        ent.MaxPairs.Should().Be(int.MaxValue);
        ent.MaxConnectedAccounts.Should().Be(int.MaxValue);
        ent.MinSyncIntervalMinutes.Should().Be(1);
        ent.EnabledModules.Should().BeEquivalentTo(new[] { "calendar", "files", "clipboard" });
    }

    [Fact]
    public async Task Toggle_on_keeps_cloud_fallback_unlocked()
    {
        using var harness = new EfStoreTestHarness();
        await using (var db = harness.NewContext())
        {
            db.UserToggles.Add(new UserToggleRow { UserId = "default", CloudFallbackSync = true });
            await db.SaveChangesAsync();
        }

        var service = new DefaultEntitlementsService(harness.Factory);
        var ent = await service.GetForUserAsync("default", CancellationToken.None);

        ent.CloudFallbackSync.Should().BeTrue();
    }
}
