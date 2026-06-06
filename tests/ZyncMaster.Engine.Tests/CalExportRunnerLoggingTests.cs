using System;
using FluentAssertions;
using ZyncMaster.Engine;
using Xunit;

namespace ZyncMaster.Engine.Tests;

public sealed class CalExportRunnerLoggingTests
{
    // FIX 2 — when a CalExport child is killed for exceeding its timeout, the Error log must name the
    // probable cause (an Outlook modal dialog) and the operation + duration so a wedged sync is
    // diagnosable from the log alone.
    [Fact]
    public void TimeoutLogMessage_NamesOperationDurationAndProbableCause()
    {
        var msg = CalExportRunner.BuildTimeoutLogMessage("export", TimeSpan.FromMinutes(5));

        msg.Should().Contain("export");
        msg.Should().Contain("5");
        msg.Should().Contain("killed");
        // The probable cause — an Outlook modal prompt — is the actionable hint for the user.
        msg.Should().Contain("modal dialog");
        msg.Should().Contain("Programmatic Access");
    }

    [Fact]
    public void TimeoutLogMessage_FormatsFractionalMinutes()
    {
        var msg = CalExportRunner.BuildTimeoutLogMessage("list-calendars", TimeSpan.FromSeconds(90));

        msg.Should().Contain("list-calendars");
        msg.Should().Contain("1.5");
    }

    // A non-positive timeout falls back to the default ceiling rather than 0 (which would kill the
    // child instantly). The ctor is the only place that clamp lives.
    [Theory]
    [InlineData(0)]
    [InlineData(-7)]
    public void Ctor_NonPositiveTimeout_DoesNotThrow_FallsBackToDefault(int minutes)
    {
        Action act = () => new CalExportRunner("calexport.exe", logger: null, timeoutMinutes: minutes);
        act.Should().NotThrow();
    }

    [Fact]
    public void Ctor_NullPath_Throws()
    {
        Action act = () => new CalExportRunner(null!, logger: null, timeoutMinutes: 5);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ExitLogMessage_IncludesCodeAndFullStderr()
    {
        var stderr = "Error: COM exception 0x80004005 line 1\nstack trace line 2";

        var msg = CalExportRunner.BuildExitLogMessage(3, stderr);

        msg.Should().Contain("exited with code 3");
        // The FULL stderr must be present — this is the key diagnostic for a non-syncing pair.
        msg.Should().Contain(stderr);
    }

    [Fact]
    public void ExitLogMessage_EmptyStderr_StillReportsCode()
    {
        var msg = CalExportRunner.BuildExitLogMessage(1, "");

        msg.Should().Contain("exited with code 1");
        msg.Should().NotContain("stderr:");
    }

    // Feature 2 — the --list-calendars JSON parser projects the display names from CalExport's
    // [{displayName, entryId, storeId}] array, skipping blanks.
    [Fact]
    public void ParseCalendarNames_ProjectsDisplayNames_SkippingBlanks()
    {
        var json = """
        [
          { "displayName": "Work [w@x.com]", "entryId": "e1", "storeId": "s1" },
          { "displayName": "", "entryId": "e2", "storeId": "s2" },
          { "displayName": "Personal [p@x.com]", "entryId": "e3", "storeId": "s3" }
        ]
        """;

        var names = CalExportRunner.ParseCalendarNames(json);

        names.Should().BeEquivalentTo(new[] { "Work [w@x.com]", "Personal [p@x.com]" }, o => o.WithStrictOrdering());
    }

    [Fact]
    public void ParseCalendarNames_EmptyArray_ReturnsEmpty()
    {
        CalExportRunner.ParseCalendarNames("[]").Should().BeEmpty();
    }
}
