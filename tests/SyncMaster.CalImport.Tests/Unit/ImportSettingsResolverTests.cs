using System;
using FluentAssertions;
using SyncMaster.CalImport;
using Xunit;

namespace SyncMaster.CalImport.Tests;

public sealed class ImportSettingsResolverTests
{
    private readonly ImportSettingsResolver _sut = new ImportSettingsResolver();

    // ── ResolveAuthority (AppConfig) ──────────────────────────────────────

    [Fact]
    public void ResolveAuthority_Empty_ReturnsDefault()
    {
        var c = new AppConfig { Authority = "" };
        _sut.ResolveAuthority(c).Should().Be("https://login.microsoftonline.com/consumers");
    }

    [Fact]
    public void ResolveAuthority_Whitespace_ReturnsDefault()
    {
        var c = new AppConfig { Authority = "   " };
        _sut.ResolveAuthority(c).Should().Be("https://login.microsoftonline.com/consumers");
    }

    [Fact]
    public void ResolveAuthority_KeepsCustomValue()
    {
        var c = new AppConfig { Authority = "https://login.microsoftonline.com/organizations" };
        _sut.ResolveAuthority(c).Should().Be("https://login.microsoftonline.com/organizations");
    }

    [Fact]
    public void ResolveAuthority_InvalidUri_Throws()
    {
        var c = new AppConfig { Authority = "not-a-url" };
        Action act = () => _sut.ResolveAuthority(c);
        act.Should().Throw<SettingsValidationException>().WithMessage("*not-a-url*");
    }

    [Fact]
    public void ResolveAuthority_NullConfig_Throws()
    {
        Action act = () => _sut.ResolveAuthority(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── ResolveExtendedPropertyGuid (AppConfig) ───────────────────────────

    [Fact]
    public void ResolveExtendedPropertyGuid_ParsesValidGuid()
    {
        var guid = "ab123456-7890-1234-5678-90abcdef1234";
        var c = new AppConfig { ExtendedPropertyGuid = guid };
        _sut.ResolveExtendedPropertyGuid(c).Should().Be(Guid.Parse(guid));
    }

    [Fact]
    public void ResolveExtendedPropertyGuid_ThrowsOnInvalid()
    {
        var c = new AppConfig { ExtendedPropertyGuid = "not-a-guid" };
        Action act = () => _sut.ResolveExtendedPropertyGuid(c);
        act.Should().Throw<SettingsValidationException>()
            .WithMessage("*not-a-guid*")
            .WithMessage("*extendedPropertyGuid*")
            .WithMessage("*WARNING*");
    }

    [Fact]
    public void ResolveExtendedPropertyGuid_EmptyString_ReturnsDefault()
    {
        var c = new AppConfig { ExtendedPropertyGuid = "" };
        _sut.ResolveExtendedPropertyGuid(c).Should().Be(new Guid(AppConfig.DefaultExtendedPropertyGuid));
    }

    [Fact]
    public void ResolveExtendedPropertyGuid_NullString_ReturnsDefault()
    {
        var c = new AppConfig { ExtendedPropertyGuid = null! };
        _sut.ResolveExtendedPropertyGuid(c).Should().Be(new Guid(AppConfig.DefaultExtendedPropertyGuid));
    }

    [Fact]
    public void ResolveExtendedPropertyGuid_NullConfig_Throws()
    {
        Action act = () => _sut.ResolveExtendedPropertyGuid(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── ResolveReminderMinutes (ImportSettings) ───────────────────────────

    [Fact]
    public void ResolveReminderMinutes_Zero_ReturnsZero()
    {
        _sut.ResolveReminderMinutes(new ImportSettings { ReminderMinutes = 0 }).Should().Be(0);
    }

    [Fact]
    public void ResolveReminderMinutes_Negative_FallsBackTo30()
    {
        _sut.ResolveReminderMinutes(new ImportSettings { ReminderMinutes = -5 }).Should().Be(30);
    }

    [Fact]
    public void ResolveReminderMinutes_Valid_Kept()
    {
        _sut.ResolveReminderMinutes(new ImportSettings { ReminderMinutes = 15 }).Should().Be(15);
    }

    [Fact]
    public void ResolveReminderMinutes_NullSettings_Throws()
    {
        Action act = () => _sut.ResolveReminderMinutes(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
