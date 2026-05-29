using System;
using ZyncMaster.CalExport;
using ZyncMaster.Core;
using FluentAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ZyncMaster.CalExport.Tests;

public sealed class SettingsResolverTests
{
    private readonly SettingsResolver _sut = new SettingsResolver();

    // ── Year ──────────────────────────────────────────────────────────────

    [Fact]
    public void ResolveYear_Current_ReturnsThisYear()
    {
        var settings = new AppSettings { Year = new JValue("current") };

        _sut.ResolveYear(settings).Should().Be(DateTime.Today.Year);
    }

    [Fact]
    public void ResolveYear_Previous_ReturnsLastYear()
    {
        var settings = new AppSettings { Year = new JValue("previous") };

        _sut.ResolveYear(settings).Should().Be(DateTime.Today.Year - 1);
    }

    [Fact]
    public void ResolveYear_IntegerToken_ReturnsIt()
    {
        var settings = new AppSettings { Year = new JValue(2025) };

        _sut.ResolveYear(settings).Should().Be(2025);
    }

    [Fact]
    public void ResolveYear_NumericString_ReturnsIt()
    {
        var settings = new AppSettings { Year = new JValue("2025") };

        _sut.ResolveYear(settings).Should().Be(2025);
    }

    [Fact]
    public void ResolveYear_InvalidString_ReturnsTodayYear()
    {
        var settings = new AppSettings { Year = new JValue("notayear") };

        _sut.ResolveYear(settings).Should().Be(DateTime.Today.Year);
    }

    // ── Month ─────────────────────────────────────────────────────────────

    [Fact]
    public void ResolveMonth_Current_ReturnsTodayMonth()
    {
        var settings = new AppSettings { Month = new JValue("current") };

        _sut.ResolveMonth(settings).Should().Be(DateTime.Today.Month);
    }

    [Fact]
    public void ResolveMonth_Previous_ReturnsLastMonth()
    {
        var settings = new AppSettings { Month = new JValue("previous") };

        _sut.ResolveMonth(settings).Should().Be(DateTime.Today.AddMonths(-1).Month);
    }

    [Fact]
    public void ResolveMonth_ValidInt_ReturnsIt()
    {
        var settings = new AppSettings { Month = new JValue(5) };

        _sut.ResolveMonth(settings).Should().Be(5);
    }

    [Fact]
    public void ResolveMonth_OutOfRangeInt_ReturnsTodayMonth()
    {
        var settings = new AppSettings { Month = new JValue(13) };

        _sut.ResolveMonth(settings).Should().Be(DateTime.Today.Month);
    }

    [Fact]
    public void ResolveMonth_Zero_ReturnsTodayMonth()
    {
        var settings = new AppSettings { Month = new JValue(0) };

        _sut.ResolveMonth(settings).Should().Be(DateTime.Today.Month);
    }

    // ── Mode ──────────────────────────────────────────────────────────────

    [Fact]
    public void ResolveMode_Simple_ReturnsSimple()
    {
        var settings = new AppSettings { Mode = "simple" };

        _sut.ResolveMode(settings).Should().Be(ExportMode.Simple);
    }

    [Fact]
    public void ResolveMode_Complete_ReturnsComplete()
    {
        var settings = new AppSettings { Mode = "complete" };

        _sut.ResolveMode(settings).Should().Be(ExportMode.Complete);
    }

    [Fact]
    public void ResolveMode_SimpleUppercase_ReturnsSimple()
    {
        var settings = new AppSettings { Mode = "SIMPLE" };

        _sut.ResolveMode(settings).Should().Be(ExportMode.Simple);
    }

    [Fact]
    public void ResolveMode_Unknown_ReturnsComplete()
    {
        var settings = new AppSettings { Mode = "other" };

        _sut.ResolveMode(settings).Should().Be(ExportMode.Complete);
    }

    [Fact]
    public void ResolveMonth_CurrentUppercase_ReturnsTodayMonth()
    {
        var settings = new AppSettings { Month = new JValue("CURRENT") };

        _sut.ResolveMonth(settings).Should().Be(DateTime.Today.Month);
    }

    [Fact]
    public void ResolveMonth_PreviousUppercase_ReturnsLastMonth()
    {
        var settings = new AppSettings { Month = new JValue("PREVIOUS") };

        _sut.ResolveMonth(settings).Should().Be(DateTime.Today.AddMonths(-1).Month);
    }

    // ── Calendar names ────────────────────────────────────────────────────

    [Fact]
    public void ResolveCalendarNames_All_ReturnsNull()
    {
        var settings = new AppSettings { Calendars = new JValue("all") };

        _sut.ResolveCalendarNames(settings).Should().BeNull();
    }

    [Fact]
    public void ResolveCalendarNames_AllUppercase_ReturnsNull()
    {
        var settings = new AppSettings { Calendars = new JValue("ALL") };

        _sut.ResolveCalendarNames(settings).Should().BeNull();
    }

    [Fact]
    public void ResolveCalendarNames_JArray_ReturnsStringArray()
    {
        var settings = new AppSettings
        {
            Calendars = new JArray("Cal1", "Cal2"),
        };

        var result = _sut.ResolveCalendarNames(settings);

        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(new[] { "Cal1", "Cal2" });
    }

    // ── Null settings guards ──────────────────────────────────────────────

    [Fact]
    public void ResolveYear_NullSettings_Throws()
    {
        Action act = () => _sut.ResolveYear(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("settings");
    }

    [Fact]
    public void ResolveMonth_NullSettings_Throws()
    {
        Action act = () => _sut.ResolveMonth(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("settings");
    }

    [Fact]
    public void ResolveMode_NullSettings_Throws()
    {
        Action act = () => _sut.ResolveMode(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("settings");
    }

    [Fact]
    public void ResolveCalendarNames_NullSettings_Throws()
    {
        Action act = () => _sut.ResolveCalendarNames(null!);
        act.Should().Throw<ArgumentNullException>().WithParameterName("settings");
    }

    // ── Non-JValue token branches ─────────────────────────────────────────

    [Fact]
    public void ResolveYear_JArrayToken_ReturnsTodayYear()
    {
        var settings = new AppSettings { Year = new JArray(2025) };

        _sut.ResolveYear(settings).Should().Be(DateTime.Today.Year);
    }

    [Fact]
    public void ResolveYear_JObjectToken_ReturnsTodayYear()
    {
        var settings = new AppSettings { Year = new JObject { ["x"] = 1 } };

        _sut.ResolveYear(settings).Should().Be(DateTime.Today.Year);
    }

    [Fact]
    public void ResolveMonth_JArrayToken_ReturnsTodayMonth()
    {
        var settings = new AppSettings { Month = new JArray(5) };

        _sut.ResolveMonth(settings).Should().Be(DateTime.Today.Month);
    }

    [Fact]
    public void ResolveMonth_JObjectToken_ReturnsTodayMonth()
    {
        var settings = new AppSettings { Month = new JObject { ["x"] = 1 } };

        _sut.ResolveMonth(settings).Should().Be(DateTime.Today.Month);
    }

    [Fact]
    public void ResolveMonth_NonParsableString_ReturnsTodayMonth()
    {
        var settings = new AppSettings { Month = new JValue("not-a-month") };

        _sut.ResolveMonth(settings).Should().Be(DateTime.Today.Month);
    }

    [Fact]
    public void ResolveMonth_NumericStringInRange_ReturnsIt()
    {
        var settings = new AppSettings { Month = new JValue("7") };

        _sut.ResolveMonth(settings).Should().Be(7);
    }

    [Fact]
    public void ResolveMonth_NumericStringOutOfRange_ReturnsTodayMonth()
    {
        var settings = new AppSettings { Month = new JValue("99") };

        _sut.ResolveMonth(settings).Should().Be(DateTime.Today.Month);
    }

    // ── Mode null branch ──────────────────────────────────────────────────

    [Fact]
    public void ResolveMode_NullMode_ReturnsComplete()
    {
        var settings = new AppSettings { Mode = null! };

        _sut.ResolveMode(settings).Should().Be(ExportMode.Complete);
    }

    [Fact]
    public void ResolveMode_EmptyString_ReturnsComplete()
    {
        var settings = new AppSettings { Mode = "" };

        _sut.ResolveMode(settings).Should().Be(ExportMode.Complete);
    }

    // ── Calendar names extra branches ─────────────────────────────────────

    [Fact]
    public void ResolveCalendarNames_NullToken_ReturnsNull()
    {
        var settings = new AppSettings { Calendars = null! };

        _sut.ResolveCalendarNames(settings).Should().BeNull();
    }

    [Fact]
    public void ResolveCalendarNames_StringNotAll_TriesToDeserializeAndCatches()
    {
        // A JValue with a non-"all" string is not parseable as string[];
        // Newtonsoft throws JsonSerializationException which the resolver swallows.
        var settings = new AppSettings { Calendars = new JValue("Personal") };

        _sut.ResolveCalendarNames(settings).Should().BeNull();
    }

    [Fact]
    public void ResolveCalendarNames_JObjectToken_ReturnsNullDueToCatch()
    {
        var settings = new AppSettings { Calendars = new JObject { ["x"] = 1 } };

        _sut.ResolveCalendarNames(settings).Should().BeNull();
    }
}
