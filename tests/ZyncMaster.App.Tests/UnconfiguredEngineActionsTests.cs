using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using ZyncMaster.App.Bridge;
using ZyncMaster.App.Configuration;
using ZyncMaster.Core;
using Xunit;

namespace ZyncMaster.App.Tests;

// The "configure first" engine stub: device-name checks must degrade quietly (report not
// available) rather than throw, so the UI's live ✓/✗ indicator shows a calm ✗ before the engine
// is configured instead of surfacing an error.
public class UnconfiguredEngineActionsTests
{
    private static UnconfiguredEngineActions Build()
    {
        var repo = new Mock<ISettingsRepository<AppSettings>>().Object;
        return new UnconfiguredEngineActions(repo, "unused.json");
    }

    [Fact]
    public async Task CheckDeviceName_returns_false_without_throwing()
    {
        var engine = Build();

        var available = await engine.CheckDeviceNameAsync("Anything", CancellationToken.None);

        available.Should().BeFalse();
    }
}
