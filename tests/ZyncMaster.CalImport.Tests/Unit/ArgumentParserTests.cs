using System;
using FluentAssertions;
using ZyncMaster.CalImport;
using Xunit;

namespace ZyncMaster.CalImport.Tests;

public sealed class ArgumentParserTests
{
    private readonly ArgumentParser _sut = new ArgumentParser();

    [Fact]
    public void NoArgs_AllDefaults()
    {
        var r = _sut.Parse(Array.Empty<string>());

        r.SourcePath.Should().BeNull();
        r.ConfigPath.Should().BeNull();
        r.CalendarId.Should().BeNull();
        r.NewCalendarName.Should().BeNull();
        r.AutoMode.Should().BeFalse();
        r.DryRun.Should().BeFalse();
        r.Overwrite.Should().BeFalse();
    }

    [Theory]
    [InlineData("-s")]
    [InlineData("--source")]
    public void SourceFlag_SetsPath(string flag)
    {
        var r = _sut.Parse(new[] { flag, "C:/x.json" });
        r.SourcePath.Should().Be("C:/x.json");
    }

    [Theory]
    [InlineData("-a")]
    [InlineData("--auto")]
    public void AutoFlag_SetsAuto(string flag)
    {
        _sut.Parse(new[] { flag }).AutoMode.Should().BeTrue();
    }

    [Theory]
    [InlineData("-c", "settings.json")]
    [InlineData("--config", "settings.json")]
    public void ConfigFlag_SetsConfigPath(string flag, string value)
    {
        _sut.Parse(new[] { flag, value }).ConfigPath.Should().Be(value);
    }

    [Theory]
    [InlineData("-k")]
    [InlineData("--calendar")]
    public void CalendarFlag_SetsCalendarId(string flag)
    {
        _sut.Parse(new[] { flag, "AAMk..." }).CalendarId.Should().Be("AAMk...");
    }

    [Theory]
    [InlineData("-n")]
    [InlineData("--new-calendar")]
    public void NewCalendarFlag_SetsName(string flag)
    {
        _sut.Parse(new[] { flag, "Imported" }).NewCalendarName.Should().Be("Imported");
    }

    [Fact]
    public void DryRunFlag_SetsDryRun()
    {
        _sut.Parse(new[] { "--dry-run" }).DryRun.Should().BeTrue();
    }

    [Theory]
    [InlineData("-w")]
    [InlineData("--overwrite")]
    public void OverwriteFlag_SetsOverwrite(string flag)
    {
        _sut.Parse(new[] { flag }).Overwrite.Should().BeTrue();
    }

    [Fact]
    public void AllFlags_Combined()
    {
        var r = _sut.Parse(new[]
        {
            "-s", "src.json",
            "-c", "cfg.json",
            "-k", "cid",
            "-a", "--dry-run"
        });
        r.SourcePath.Should().Be("src.json");
        r.ConfigPath.Should().Be("cfg.json");
        r.CalendarId.Should().Be("cid");
        r.AutoMode.Should().BeTrue();
        r.DryRun.Should().BeTrue();
    }

    [Fact]
    public void CalendarAndNewCalendar_Together_Throws()
    {
        Action act = () => _sut.Parse(new[] { "-k", "id", "-n", "Name" });
        act.Should().Throw<ArgumentParsingException>()
           .WithMessage("*mutually exclusive*");
    }

    [Theory]
    [InlineData("-s")]
    [InlineData("--source")]
    [InlineData("-c")]
    [InlineData("--config")]
    [InlineData("-k")]
    [InlineData("--calendar")]
    [InlineData("-n")]
    [InlineData("--new-calendar")]
    public void FlagWithMissingValue_Throws(string flag)
    {
        Action act = () => _sut.Parse(new[] { flag });
        act.Should().Throw<ArgumentParsingException>();
    }

    [Fact]
    public void UnknownArgument_Throws()
    {
        Action act = () => _sut.Parse(new[] { "--what" });
        act.Should().Throw<ArgumentParsingException>()
           .WithMessage("*Unknown argument*");
    }
}
