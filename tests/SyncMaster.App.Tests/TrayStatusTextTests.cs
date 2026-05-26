using FluentAssertions;
using SyncMaster.App.State;
using SyncMaster.App.Tray;
using Xunit;

namespace SyncMaster.App.Tests;

public class TrayStatusTextTests
{
    [Theory]
    [InlineData(SyncStatus.Idle, "SyncMaster — Idle")]
    [InlineData(SyncStatus.Syncing, "SyncMaster — Syncing…")]
    [InlineData(SyncStatus.Error, "SyncMaster — Error")]
    [InlineData(SyncStatus.Paused, "SyncMaster — Paused")]
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
