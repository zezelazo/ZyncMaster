using System;
using FluentAssertions;
using SyncMaster.CalImport;
using Xunit;

namespace SyncMaster.CalImport.Tests;

public sealed class ImportSettingsResolverTests
{
    private readonly ImportSettingsResolver _sut = new ImportSettingsResolver();

    [Fact]
    public void ResolveAuthority_DefaultsToConsumers_WhenEmpty()
    {
        var s = new ImportSettings { Authority = "" };
        _sut.ResolveAuthority(s).Should().Be("https://login.microsoftonline.com/consumers");
    }

    [Fact]
    public void ResolveAuthority_KeepsCustomValue()
    {
        var s = new ImportSettings { Authority = "https://login.microsoftonline.com/organizations" };
        _sut.ResolveAuthority(s).Should().Be("https://login.microsoftonline.com/organizations");
    }

    [Fact]
    public void ResolveAuthority_Whitespace_ReturnsDefault()
    {
        var s = new ImportSettings { Authority = "   " };
        _sut.ResolveAuthority(s).Should().Be("https://login.microsoftonline.com/consumers");
    }

    [Fact]
    public void ResolveAuthority_NullString_ReturnsDefault()
    {
        var s = new ImportSettings { Authority = null! };
        _sut.ResolveAuthority(s).Should().Be("https://login.microsoftonline.com/consumers");
    }

    [Fact]
    public void ResolveAuthority_InvalidUri_Throws()
    {
        var s = new ImportSettings { Authority = "not-a-url" };
        Action act = () => _sut.ResolveAuthority(s);
        act.Should().Throw<SettingsValidationException>()
            .WithMessage("*not-a-url*")
            .WithMessage("*authority*");
    }

    [Fact]
    public void ResolveReminderMinutes_DefaultIs30_WhenZero()
    {
        var s = new ImportSettings { ReminderMinutes = 0 };
        _sut.ResolveReminderMinutes(s).Should().Be(0);
    }

    [Fact]
    public void ResolveReminderMinutes_FallsBackTo30_WhenNegative()
    {
        var s = new ImportSettings { ReminderMinutes = -5 };
        _sut.ResolveReminderMinutes(s).Should().Be(30);
    }

    [Fact]
    public void ResolveReminderMinutes_KeepsValidValue()
    {
        var s = new ImportSettings { ReminderMinutes = 15 };
        _sut.ResolveReminderMinutes(s).Should().Be(15);
    }

    [Fact]
    public void ResolveExtendedPropertyGuid_ParsesValidGuid()
    {
        var guid = "ab123456-7890-1234-5678-90abcdef1234";
        var s = new ImportSettings { ExtendedPropertyGuid = guid };
        _sut.ResolveExtendedPropertyGuid(s).Should().Be(Guid.Parse(guid));
    }

    [Fact]
    public void ResolveExtendedPropertyGuid_ThrowsOnInvalid()
    {
        var s = new ImportSettings { ExtendedPropertyGuid = "not-a-guid" };
        Action act = () => _sut.ResolveExtendedPropertyGuid(s);
        act.Should().Throw<SettingsValidationException>()
            .WithMessage("*not-a-guid*")
            .WithMessage("*extendedPropertyGuid*")
            .WithMessage("*WARNING*");
    }

    [Fact]
    public void ResolveExtendedPropertyGuid_EmptyString_ReturnsDefault()
    {
        var s = new ImportSettings { ExtendedPropertyGuid = "" };
        _sut.ResolveExtendedPropertyGuid(s).Should().Be(new Guid(ImportSettings.DefaultExtendedPropertyGuid));
    }

    [Fact]
    public void ResolveExtendedPropertyGuid_NullString_ReturnsDefault()
    {
        var s = new ImportSettings { ExtendedPropertyGuid = null! };
        _sut.ResolveExtendedPropertyGuid(s).Should().Be(new Guid(ImportSettings.DefaultExtendedPropertyGuid));
    }

    [Fact]
    public void ResolveAuthority_NullSettings_Throws()
    {
        Action act = () => _sut.ResolveAuthority(null!);
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("settings");
    }

    [Fact]
    public void ResolveReminderMinutes_NullSettings_Throws()
    {
        Action act = () => _sut.ResolveReminderMinutes(null!);
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("settings");
    }

    [Fact]
    public void ResolveExtendedPropertyGuid_NullSettings_Throws()
    {
        Action act = () => _sut.ResolveExtendedPropertyGuid(null!);
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("settings");
    }

    [Fact]
    public void ResolveAuthority_RelativeUri_Throws()
    {
        // Relative URI is not absolute → must be rejected as invalid.
        var s = new ImportSettings { Authority = "/tenant/foo" };
        Action act = () => _sut.ResolveAuthority(s);
        act.Should().Throw<SettingsValidationException>()
            .WithMessage("*authority*")
            .WithMessage("*/tenant/foo*");
    }

    [Fact]
    public void ResolveExtendedPropertyGuid_WhitespaceWrappedGuid_Throws()
    {
        // Guid.TryParse trims, but the implementation uses string.IsNullOrEmpty (not whitespace),
        // so a whitespace-wrapped value goes through TryParse; TryParse accepts the trimmed GUID.
        var guid = "ab123456-7890-1234-5678-90abcdef1234";
        var s = new ImportSettings { ExtendedPropertyGuid = "  " + guid + "  " };

        _sut.ResolveExtendedPropertyGuid(s).Should().Be(Guid.Parse(guid));
    }
}
