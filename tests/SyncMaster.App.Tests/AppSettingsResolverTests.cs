using System;
using FluentAssertions;
using SyncMaster.App;
using SyncMaster.App.Configuration;
using Xunit;

namespace SyncMaster.App.Tests;

public class AppSettingsResolverTests
{
    private static AppSettings Valid() => new() { ServerBaseUrl = "https://sync.example.com" };

    [Fact]
    public void Throws_when_settings_null()
    {
        var resolver = new AppSettingsResolver();

        var act = () => resolver.Resolve(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Throws_validation_when_server_base_url_missing()
    {
        var resolver = new AppSettingsResolver();

        var act = () => resolver.Resolve(new AppSettings { ServerBaseUrl = "  " });

        act.Should().Throw<SettingsValidationException>()
            .WithMessage("*serverBaseUrl*");
    }

    [Fact]
    public void Applies_defaults_when_only_server_url_given()
    {
        var resolver = new AppSettingsResolver();

        var engine = resolver.Resolve(Valid());

        engine.ServerBaseUrl.Should().Be("https://sync.example.com");
        engine.DeviceName.Should().Be(Environment.MachineName);
        engine.SyncWindowDays.Should().Be(14);
        engine.IntervalMinutes.Should().Be(10);
        engine.CalExportPath.Should().Be("CalExport.exe");
        engine.CalendarNames.Should().BeNull();
    }

    [Fact]
    public void Honours_overrides()
    {
        var resolver = new AppSettingsResolver();
        var settings = new AppSettings
        {
            ServerBaseUrl = "https://x.test/",
            DeviceName = "MyLaptop",
            SyncWindowDays = 30,
            IntervalMinutes = 5,
            CalExportPath = @"C:\tools\CalExport.exe",
            Calendars = new[] { "Work", "Personal" },
        };

        var engine = resolver.Resolve(settings);

        engine.ServerBaseUrl.Should().Be("https://x.test/");
        engine.DeviceName.Should().Be("MyLaptop");
        engine.SyncWindowDays.Should().Be(30);
        engine.IntervalMinutes.Should().Be(5);
        engine.CalExportPath.Should().Be(@"C:\tools\CalExport.exe");
        engine.CalendarNames.Should().BeEquivalentTo("Work", "Personal");
    }

    [Fact]
    public void Trims_server_base_url()
    {
        var resolver = new AppSettingsResolver();

        var engine = resolver.Resolve(new AppSettings { ServerBaseUrl = "  https://trim.me  " });

        engine.ServerBaseUrl.Should().Be("https://trim.me");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Clamps_sync_window_days_up_to_one(int value)
    {
        var resolver = new AppSettingsResolver();

        var engine = resolver.Resolve(new AppSettings { ServerBaseUrl = "https://x", SyncWindowDays = value });

        engine.SyncWindowDays.Should().Be(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Clamps_interval_minutes_up_to_one(int value)
    {
        var resolver = new AppSettingsResolver();

        var engine = resolver.Resolve(new AppSettings { ServerBaseUrl = "https://x", IntervalMinutes = value });

        engine.IntervalMinutes.Should().Be(1);
    }

    [Fact]
    public void Empty_device_name_falls_back_to_machine_name()
    {
        var resolver = new AppSettingsResolver();

        var engine = resolver.Resolve(new AppSettings { ServerBaseUrl = "https://x", DeviceName = "   " });

        engine.DeviceName.Should().Be(Environment.MachineName);
    }

    [Fact]
    public void Blank_calendar_entries_are_dropped_and_empty_becomes_null()
    {
        var resolver = new AppSettingsResolver();

        var withBlanks = resolver.Resolve(new AppSettings
        {
            ServerBaseUrl = "https://x",
            Calendars = new[] { "Work", "  ", "" },
        });
        withBlanks.CalendarNames.Should().BeEquivalentTo("Work");

        var allBlank = resolver.Resolve(new AppSettings
        {
            ServerBaseUrl = "https://x",
            Calendars = new[] { "  ", "" },
        });
        allBlank.CalendarNames.Should().BeNull();
    }
}
