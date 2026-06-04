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

    [Fact]
    public async Task ExportSourceTxt_degrades_to_null_like_GenerateTxt()
    {
        // Both .txt export branches must behave identically when unconfigured: GenerateTxt (COM)
        // returns null (cancelled), so the Graph branch must too — never throw — so the UI shows the
        // clean "Save cancelled" path instead of a red error.
        var engine = Build();

        var generate = await engine.GenerateTxtAsync("{}", CancellationToken.None);
        var export = await engine.ExportSourceTxtAsync("{\"pairId\":\"p1\"}", CancellationToken.None);

        generate.Should().BeNull();
        export.Should().BeNull();
    }

    [Fact]
    public async Task CancelConnect_is_a_quiet_no_op()
    {
        // No connect can be in flight without a configured engine, so cancelling must complete
        // quietly (mirrors CancelLoginAsync) rather than throwing.
        var engine = Build();

        var act = async () => await engine.CancelConnectAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
