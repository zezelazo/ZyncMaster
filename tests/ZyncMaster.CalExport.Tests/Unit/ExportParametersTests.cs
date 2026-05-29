using System;
using ZyncMaster.CalExport;
using ZyncMaster.Core;
using FluentAssertions;
using Xunit;

namespace ZyncMaster.CalExport.Tests;

public sealed class ExportParametersTests
{
    [Fact]
    public void ValidParameters_CreatesSuccessfully()
    {
        var sut = new ExportParameters(2025, 5, ExportMode.Complete, true);
        sut.Year.Should().Be(2025);
        sut.Month.Should().Be(5);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void InvalidYear_ThrowsArgumentOutOfRangeException(int year)
    {
        Action act = () => new ExportParameters(year, 5, ExportMode.Simple, false);
        act.Should().Throw<ArgumentOutOfRangeException>()
           .Which.ParamName.Should().Be("year");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    [InlineData(-1)]
    [InlineData(99)]
    public void InvalidMonth_ThrowsArgumentOutOfRangeException(int month)
    {
        Action act = () => new ExportParameters(2025, month, ExportMode.Simple, false);
        act.Should().Throw<ArgumentOutOfRangeException>()
           .Which.ParamName.Should().Be("month");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(12)]
    public void ValidMonth_AcceptsValues1To12(int month)
    {
        Action act = () => new ExportParameters(2025, month, ExportMode.Simple, false);
        act.Should().NotThrow();
    }

    [Fact]
    public void NullSelectedFolders_IsAllowed()
    {
        var sut = new ExportParameters(2025, 5, ExportMode.Simple, false, null);
        sut.SelectedFolders.Should().BeNull();
        sut.Year.Should().Be(2025);
        sut.Month.Should().Be(5);
        sut.Mode.Should().Be(ExportMode.Simple);
        sut.IncludeCancelled.Should().BeFalse();
    }

    [Fact]
    public void IncludeCancelled_StoredCorrectly()
    {
        var sut = new ExportParameters(2025, 5, ExportMode.Complete, true);
        sut.IncludeCancelled.Should().BeTrue();
    }
}
