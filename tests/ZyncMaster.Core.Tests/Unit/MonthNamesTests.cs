using System;
using FluentAssertions;
using Xunit;

namespace ZyncMaster.Core.Tests;

public sealed class MonthNamesTests
{
    [Theory]
    [InlineData(1,  "January")]
    [InlineData(2,  "February")]
    [InlineData(3,  "March")]
    [InlineData(4,  "April")]
    [InlineData(5,  "May")]
    [InlineData(6,  "June")]
    [InlineData(7,  "July")]
    [InlineData(8,  "August")]
    [InlineData(9,  "September")]
    [InlineData(10, "October")]
    [InlineData(11, "November")]
    [InlineData(12, "December")]
    public void Get_ValidMonth_ReturnsCorrectName(int month, string expected)
    {
        MonthNames.Get(month).Should().Be(expected);
    }

    [Fact]
    public void Get_Zero_ThrowsArgumentOutOfRangeException()
    {
        Action act = () => MonthNames.Get(0);

        act.Should().Throw<ArgumentOutOfRangeException>()
           .WithParameterName("month");
    }

    [Fact]
    public void Get_Thirteen_ThrowsArgumentOutOfRangeException()
    {
        Action act = () => MonthNames.Get(13);

        act.Should().Throw<ArgumentOutOfRangeException>()
           .WithParameterName("month");
    }

    [Fact]
    public void All_ReturnsTwelveItems()
    {
        MonthNames.All.Count.Should().Be(12);
    }

    [Fact]
    public void All_FirstIsJanuaryLastIsDecember()
    {
        MonthNames.All[0].Should().Be("January");
        MonthNames.All[11].Should().Be("December");
    }
}
