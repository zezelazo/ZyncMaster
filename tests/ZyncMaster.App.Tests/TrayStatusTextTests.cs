using FluentAssertions;
using ZyncMaster.App.State;
using ZyncMaster.App.Tray;
using Xunit;

namespace ZyncMaster.App.Tests;

public class TrayStatusTextTests
{
    [Theory]
    [InlineData(SyncStatus.Idle, "ZyncMaster — Idle")]
    [InlineData(SyncStatus.Syncing, "ZyncMaster — Syncing…")]
    [InlineData(SyncStatus.Error, "ZyncMaster — Error")]
    [InlineData(SyncStatus.Paused, "ZyncMaster — Paused")]
    public void Header_text_reflects_each_state(SyncStatus status, string expected)
    {
        TrayStatusText.Header(status).Should().Be(expected);
    }

    [Fact]
    public void Pause_item_label_toggles()
    {
        TrayStatusText.PauseItem(false).Should().Be("Pause auto-sync");
        TrayStatusText.PauseItem(true).Should().Be("Resume auto-sync");
    }
}
