using System;
using ZyncMaster.CalExport;
using FluentAssertions;
using Xunit;

namespace ZyncMaster.CalExport.Tests;

public sealed class ArgumentParserTests
{
    private readonly ArgumentParser _sut = new ArgumentParser();

    [Fact]
    public void NoArgs_ReturnsAllDefaults()
    {
        var result = _sut.Parse(Array.Empty<string>());

        result.AutoMode.Should().BeFalse();
        result.ConfigPath.Should().BeNull();
        result.OutputPath.Should().BeNull();
        result.Verbose.Should().BeFalse();
    }

    [Fact]
    public void ShortVerboseFlag_SetsVerbose()
    {
        var result = _sut.Parse(new[] { "-v" });

        result.Verbose.Should().BeTrue();
        result.AutoMode.Should().BeFalse();
    }

    [Fact]
    public void LongVerboseFlag_SetsVerbose()
    {
        var result = _sut.Parse(new[] { "--verbose" });

        result.Verbose.Should().BeTrue();
    }

    [Fact]
    public void VerboseCombinesWithOtherFlags()
    {
        var result = _sut.Parse(new[] { "-a", "-c", "my.json", "-o", "D:\\out", "-v" });

        result.AutoMode.Should().BeTrue();
        result.ConfigPath.Should().Be("my.json");
        result.OutputPath.Should().Be("D:\\out");
        result.Verbose.Should().BeTrue();
    }

    [Fact]
    public void ShortAutoFlag_SetsAutoMode()
    {
        var result = _sut.Parse(new[] { "-a" });

        result.AutoMode.Should().BeTrue();
        result.ConfigPath.Should().BeNull();
        result.OutputPath.Should().BeNull();
    }

    [Fact]
    public void LongAutoFlag_SetsAutoMode()
    {
        var result = _sut.Parse(new[] { "--auto" });

        result.AutoMode.Should().BeTrue();
    }

    [Fact]
    public void ShortConfigFlag_SetsConfigPath()
    {
        var result = _sut.Parse(new[] { "-c", "path/to/settings.json" });

        result.ConfigPath.Should().Be("path/to/settings.json");
        result.AutoMode.Should().BeFalse();
    }

    [Fact]
    public void LongConfigFlag_SetsConfigPath()
    {
        var result = _sut.Parse(new[] { "--config", "C:\\settings.json" });

        result.ConfigPath.Should().Be("C:\\settings.json");
    }

    [Fact]
    public void ShortOutputFlag_SetsOutputPath()
    {
        var result = _sut.Parse(new[] { "-o", "D:\\exports" });

        result.OutputPath.Should().Be("D:\\exports");
        result.AutoMode.Should().BeFalse();
    }

    [Fact]
    public void LongOutputFlag_SetsOutputPath()
    {
        var result = _sut.Parse(new[] { "--output", "D:\\exports" });

        result.OutputPath.Should().Be("D:\\exports");
    }

    [Fact]
    public void AllThreeFlags_SetsAll()
    {
        var result = _sut.Parse(new[] { "-a", "-c", "my.json", "-o", "D:\\out" });

        result.AutoMode.Should().BeTrue();
        result.ConfigPath.Should().Be("my.json");
        result.OutputPath.Should().Be("D:\\out");
    }

    [Fact]
    public void ShortConfig_MissingValue_Throws()
    {
        Action act = () => _sut.Parse(new[] { "-c" });

        act.Should().Throw<ArgumentParsingException>()
           .WithMessage("*-c/--config*");
    }

    [Fact]
    public void LongConfig_MissingValue_Throws()
    {
        Action act = () => _sut.Parse(new[] { "--config" });

        act.Should().Throw<ArgumentParsingException>()
           .WithMessage("*-c/--config*");
    }

    [Fact]
    public void ShortOutput_MissingValue_Throws()
    {
        Action act = () => _sut.Parse(new[] { "-o" });

        act.Should().Throw<ArgumentParsingException>()
           .WithMessage("*-o/--output*");
    }

    [Fact]
    public void LongOutput_MissingValue_Throws()
    {
        Action act = () => _sut.Parse(new[] { "--output" });

        act.Should().Throw<ArgumentParsingException>()
           .WithMessage("*-o/--output*");
    }

    [Fact]
    public void NoArgs_ListCalendarsIsFalse()
    {
        _sut.Parse(Array.Empty<string>()).ListCalendars.Should().BeFalse();
    }

    [Fact]
    public void ShortListCalendarsFlag_SetsListCalendars()
    {
        var result = _sut.Parse(new[] { "-l" });

        result.ListCalendars.Should().BeTrue();
        result.AutoMode.Should().BeFalse();
    }

    [Fact]
    public void LongListCalendarsFlag_SetsListCalendars()
    {
        _sut.Parse(new[] { "--list-calendars" }).ListCalendars.Should().BeTrue();
    }

    [Fact]
    public void ListCalendarsCombinesWithOutputAndVerbose()
    {
        var result = _sut.Parse(new[] { "-l", "-o", "D:\\out", "-v" });

        result.ListCalendars.Should().BeTrue();
        result.OutputPath.Should().Be("D:\\out");
        result.Verbose.Should().BeTrue();
    }

    [Fact]
    public void UnknownFlag_Throws()
    {
        Action act = () => _sut.Parse(new[] { "--unknown" });

        act.Should().Throw<ArgumentParsingException>()
           .WithMessage("*Unknown argument*");
    }

    [Fact]
    public void UnknownShortFlag_Throws()
    {
        Action act = () => _sut.Parse(new[] { "-z" });

        act.Should().Throw<ArgumentParsingException>()
           .WithMessage("*Unknown argument*");
    }
}
