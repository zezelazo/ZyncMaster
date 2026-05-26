using System;
using System.Collections.Generic;
using FluentAssertions;
using Moq;
using SyncMaster.CalImport;
using SyncMaster.Core;
using SyncMaster.Graph;
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
    public void PromptSelection_NullCalendars_Throws()
    {
        Action act = () => BuildSut().PromptSelection(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void PromptSelection_NoCalendars_Terminates()
    {
        Action act = () => BuildSut().PromptSelection(Array.Empty<CalendarTargetInfo>());
        act.Should().Throw<TerminatedException>();
    }

    [Fact]
    public void PromptSelection_ValidNumber_ReturnsThatCalendar()
    {
        _console.Setup(c => c.ReadLine()).Returns("2");
        var cals   = Calendars(("a", "AAA", true), ("b", "BBB", false));

        var chosen = BuildSut().PromptSelection(cals);

        chosen.IsCreateNew.Should().BeFalse();
        chosen.Existing!.Id.Should().Be("b");
    }

    [Fact]
    public void PromptSelection_InvalidInput_Terminates()
    {
        _console.Setup(c => c.ReadLine()).Returns("not a number");
        var cals = Calendars(("a", "AAA", true));

        Action act = () => BuildSut().PromptSelection(cals);

        act.Should().Throw<TerminatedException>();
    }

    [Fact]
    public void PromptSelection_OutOfRangeNumber_Terminates()
    {
        _console.Setup(c => c.ReadLine()).Returns("9");
        var cals = Calendars(("a", "AAA", true), ("b", "BBB", false));

        Action act = () => BuildSut().PromptSelection(cals);

        act.Should().Throw<TerminatedException>();
    }

    [Fact]
    public void PromptSelection_ChooseN_PromptsNameAndReturnsCreateNew()
    {
        _console.SetupSequence(c => c.ReadLine()).Returns("N").Returns("Trabajo Importado");
        var cals = Calendars(("a", "AAA", true), ("b", "BBB", false));

        var chosen = BuildSut().PromptSelection(cals);

        chosen.IsCreateNew.Should().BeTrue();
        chosen.NewCalendarName.Should().Be("Trabajo Importado");
        chosen.Existing.Should().BeNull();
    }

    [Fact]
    public void PromptSelection_ChooseN_Lowercase_Works()
    {
        _console.SetupSequence(c => c.ReadLine()).Returns("n").Returns("MyCal");
        var cals = Calendars(("a", "AAA", true));

        var chosen = BuildSut().PromptSelection(cals);

        chosen.IsCreateNew.Should().BeTrue();
        chosen.NewCalendarName.Should().Be("MyCal");
    }

    [Fact]
    public void PromptSelection_ChooseN_EmptyName_Terminates()
    {
        _console.SetupSequence(c => c.ReadLine()).Returns("N").Returns("   ");
        var cals = Calendars(("a", "AAA", true));

        Action act = () => BuildSut().PromptSelection(cals);

        act.Should().Throw<TerminatedException>();
    }
}
