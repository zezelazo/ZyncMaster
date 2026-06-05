using FluentAssertions;
using ZyncMaster.Engine;
using Xunit;

namespace ZyncMaster.Engine.Tests;

public sealed class CalExportRunnerLoggingTests
{
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
