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

    [Theory]
    [InlineData(45, 45)]
    [InlineData(-1, 0)]
    [InlineData(200, 100)]
    public async Task SetPastePanelOpacity_persists_clamped_even_when_unconfigured(int input, int expected)
    {
        // Paste-panel opacity is an App-local UI setting, so it must persist to disk even before the
        // server URL is configured (the user can set it from the Settings screen pre-config).
        var repo = new Mock<ISettingsRepository<AppSettings>>();
        repo.Setup(r => r.TryLoad("unused.json")).Returns((AppSettings?)null);
        AppSettings? saved = null;
        repo.Setup(r => r.Save(It.IsAny<AppSettings>(), "unused.json"))
            .Callback<AppSettings, string>((s, _) => saved = s);
        var engine = new UnconfiguredEngineActions(repo.Object, "unused.json");

        await engine.SetPastePanelOpacityAsync(input, CancellationToken.None);

        saved.Should().NotBeNull();
        saved!.PastePanelOpacity.Should().Be(expected);
    }

    [Fact]
    public async Task CalendarV2_actions_degrade_clearly_when_unconfigured()
    {
        var sut = Build();

        (await sut.ListPrefixRulesAsync()).Should().Be("[]");
        await ((System.Func<Task>)(() => sut.GetCalendarDayAsync("2026-06-10")))
            .Should().ThrowAsync<System.InvalidOperationException>().WithMessage("*Settings*");
        await ((System.Func<Task>)(() => sut.SavePrefixRuleAsync("{}")))
            .Should().ThrowAsync<System.InvalidOperationException>().WithMessage("*Settings*");
    }

    [Fact]
    public async Task HasIdentity_is_false_when_unconfigured()
    {
        // No engine → no identity can be in use, so the cheap presence gate reports false (the
        // clipboard boot then quietly waits for sign-in instead of throwing).
        var engine = Build();

        (await engine.HasIdentityAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task GetClipboardDevices_surfaces_persisted_opacity_when_unconfigured()
    {
        var repo = new Mock<ISettingsRepository<AppSettings>>();
        repo.Setup(r => r.TryLoad("unused.json"))
            .Returns(new AppSettings { PastePanelOpacity = 40 });
        var engine = new UnconfiguredEngineActions(repo.Object, "unused.json");

        var view = await engine.GetClipboardDevicesAsync(CancellationToken.None);

        view.PastePanelOpacity.Should().Be(40);
    }

    [Fact]
    public async Task GetAppVersion_returns_a_version_string_without_throwing()
    {
        // The test runner IS the entry assembly, so the result can be empty or a version — what
        // matters is that it never throws and always returns a string (not null).
        var engine = Build();

        var result = await engine.GetAppVersionAsync(CancellationToken.None);

        result.Should().NotBeNull();
        result.Should().NotContain("+"); // build-metadata suffix must be stripped
    }
}
