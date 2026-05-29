using FluentAssertions;
using ZyncMaster.App.State;
using Xunit;

namespace ZyncMaster.App.Tests;

public class AppStatusTests
{
    [Fact]
    public void Default_status_is_idle()
    {
        var status = new AppStatus();

        status.Status.Should().Be(SyncStatus.Idle);
    }

    [Fact]
    public void Default_flags_are_false_and_counts_zero()
    {
        var status = new AppStatus();

        status.Paired.Should().BeFalse();
        status.Paused.Should().BeFalse();
        status.NoConnectedAccount.Should().BeFalse();
        status.Created.Should().Be(0);
        status.Updated.Should().Be(0);
        status.Deleted.Should().Be(0);
        status.Skipped.Should().Be(0);
    }

    [Fact]
    public void Default_optional_fields_are_null()
    {
        var status = new AppStatus();

        status.PairingCode.Should().BeNull();
        status.LastMessage.Should().BeNull();
        status.LastSyncUtc.Should().BeNull();
    }

    [Fact]
    public void Records_with_same_values_are_equal()
    {
        var a = new AppStatus { Status = SyncStatus.Syncing, Paired = true, Created = 3 };
        var b = new AppStatus { Status = SyncStatus.Syncing, Paired = true, Created = 3 };

        a.Should().Be(b);
    }
}
