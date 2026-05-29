using System;
using FluentAssertions;
using ZyncMaster.Cli;
using Xunit;

namespace ZyncMaster.Engine.Tests;

public sealed class CliSettingsResolverTests
{
    private static CliSettings Valid() => new CliSettings
    {
        ServerBaseUrl = "https://sync.example.com",
    };

    [Fact]
    public void Resolve_NullSettings_Throws()
    {
        Action act = () => new CliSettingsResolver().Resolve(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Resolve_DefaultsApplied_WhenFieldsEmpty()
    {
        var settings = Valid();
        settings.DeviceName = null;
        settings.CalExportPath = null;
        settings.Calendars = null;

        var engine = new CliSettingsResolver().Resolve(settings);

        engine.ServerBaseUrl.Should().Be("https://sync.example.com");
        engine.DeviceName.Should().Be(Environment.MachineName);
        engine.SyncWindowDays.Should().Be(14);
        engine.IntervalMinutes.Should().Be(10);
        engine.CalExportPath.Should().Be("ZyncMaster.CalExport.exe");
        engine.CalendarNames.Should().BeNull();
    }

    [Fact]
    public void Resolve_ExplicitOverrides_Honored()
    {
        var settings = new CliSettings
        {
            ServerBaseUrl = "https://srv.test",
            DeviceName = "Front-Desk",
            SyncWindowDays = 30,
            IntervalMinutes = 5,
            CalExportPath = @"C:\tools\CalExport.exe",
            Calendars = new[] { "Work", "Personal" },
        };

        var engine = new CliSettingsResolver().Resolve(settings);

        engine.DeviceName.Should().Be("Front-Desk");
        engine.SyncWindowDays.Should().Be(30);
        engine.IntervalMinutes.Should().Be(5);
        engine.CalExportPath.Should().Be(@"C:\tools\CalExport.exe");
        engine.CalendarNames.Should().BeEquivalentTo(new[] { "Work", "Personal" });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-7)]
    public void Resolve_IntervalMinutes_ClampedToOne(int interval)
    {
        var settings = Valid();
        settings.IntervalMinutes = interval;

        var engine = new CliSettingsResolver().Resolve(settings);

        engine.IntervalMinutes.Should().Be(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Resolve_SyncWindowDays_ClampedToOne(int days)
    {
        var settings = Valid();
        settings.SyncWindowDays = days;

        var engine = new CliSettingsResolver().Resolve(settings);

        engine.SyncWindowDays.Should().Be(1);
    }

    [Fact]
    public void Resolve_MissingServerBaseUrl_ThrowsValidation()
    {
        var settings = new CliSettings { ServerBaseUrl = "   " };

        Action act = () => new CliSettingsResolver().Resolve(settings);

        act.Should().Throw<SettingsValidationException>()
            .WithMessage("*serverBaseUrl*");
    }
}
