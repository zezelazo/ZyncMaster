using System;
using System.Collections.Generic;
using FluentAssertions;
using Moq;
using SyncMaster.CalImport;
using SyncMaster.Core;
using Xunit;

namespace SyncMaster.CalImport.Tests;

public sealed class CalendarPickerTests
{
    private readonly Mock<IConsoleIO>             _console    = new Mock<IConsoleIO>();
    private readonly Mock<IApplicationTerminator> _terminator = new Mock<IApplicationTerminator>();
    private CalendarPicker BuildSut() => new CalendarPicker(_console.Object, _terminator.Object);

    private sealed class TerminatedException : Exception { public TerminatedException(string m) : base(m) { } }

    public CalendarPickerTests()
    {
        _terminator.Setup(t => t.ExitWithError(It.IsAny<string>(), It.IsAny<int>()))
                   .Throws<TerminatedException>(() => new TerminatedException("exit"));
    }

    private static IReadOnlyList<CalendarTargetInfo> Calendars(params (string id, string name, bool isDefault)[] items)
    {
        var list = new List<CalendarTargetInfo>();
        foreach (var (id, name, isDefault) in items)
            list.Add(new CalendarTargetInfo { Id = id, DisplayName = name, IsDefault = isDefault });
        return list;
    }

    [Fact]
    public void NoCalendars_Terminates()
    {
        Action act = () => BuildSut().Choose(
            new ParsedImportArguments(),
            new ImportSettings(),
            Array.Empty<CalendarTargetInfo>());

        act.Should().Throw<TerminatedException>();
    }

    [Fact]
    public void ExplicitCalendarId_Match_ReturnsThatCalendar()
    {
        var cals   = Calendars(("a", "AAA", true), ("b", "BBB", false));
        var chosen = BuildSut().Choose(
            new ParsedImportArguments { CalendarId = "b" },
            new ImportSettings(),
            cals);

        chosen.Id.Should().Be("b");
    }

    [Fact]
    public void ExplicitCalendarId_NoMatch_Terminates()
    {
        var cals = Calendars(("a", "AAA", true));
        Action act = () => BuildSut().Choose(
            new ParsedImportArguments { CalendarId = "missing" },
            new ImportSettings(),
            cals);

        act.Should().Throw<TerminatedException>();
    }

    [Fact]
    public void AutoMode_WithDefaultIdInSettings_ReturnsIt()
    {
        var cals   = Calendars(("a", "AAA", true), ("b", "BBB", false));
        var chosen = BuildSut().Choose(
            new ParsedImportArguments { AutoMode = true },
            new ImportSettings { DefaultCalendarId = "b" },
            cals);

        chosen.Id.Should().Be("b");
    }

    [Fact]
    public void AutoMode_DefaultIdNotFound_Terminates()
    {
        var cals = Calendars(("a", "AAA", true));
        Action act = () => BuildSut().Choose(
            new ParsedImportArguments { AutoMode = true },
            new ImportSettings { DefaultCalendarId = "missing" },
            cals);

        act.Should().Throw<TerminatedException>();
    }

    [Fact]
    public void AutoMode_NoDefault_PicksDefaultCalendar()
    {
        var cals   = Calendars(("a", "AAA", false), ("b", "BBB", true));
        var chosen = BuildSut().Choose(
            new ParsedImportArguments { AutoMode = true },
            new ImportSettings(),
            cals);

        chosen.Id.Should().Be("b");
    }

    [Fact]
    public void Interactive_PromptsAndAcceptsValidNumber()
    {
        _console.Setup(c => c.ReadLine()).Returns("2");
        var cals   = Calendars(("a", "AAA", true), ("b", "BBB", false));
        var chosen = BuildSut().Choose(
            new ParsedImportArguments(),
            new ImportSettings(),
            cals);

        chosen.Id.Should().Be("b");
    }

    [Fact]
    public void Interactive_InvalidInput_Terminates()
    {
        _console.Setup(c => c.ReadLine()).Returns("not a number");
        var cals = Calendars(("a", "AAA", true));
        Action act = () => BuildSut().Choose(
            new ParsedImportArguments(),
            new ImportSettings(),
            cals);

        act.Should().Throw<TerminatedException>();
    }

    [Fact]
    public void Interactive_DefaultIdNotFound_UserConfirms_FallsToList()
    {
        _console.SetupSequence(c => c.ReadLine()).Returns("y").Returns("1");
        var cals   = Calendars(("a", "AAA", true), ("b", "BBB", false));
        var chosen = BuildSut().Choose(
            new ParsedImportArguments(),
            new ImportSettings { DefaultCalendarId = "missing" },
            cals);

        chosen.Id.Should().Be("a");
    }

    [Fact]
    public void Interactive_DefaultIdNotFound_UserRefuses_Terminates()
    {
        _console.Setup(c => c.ReadLine()).Returns("n");
        var cals = Calendars(("a", "AAA", true));
        Action act = () => BuildSut().Choose(
            new ParsedImportArguments(),
            new ImportSettings { DefaultCalendarId = "missing" },
            cals);

        act.Should().Throw<TerminatedException>();
    }
}
