using System;
using System.Collections.Generic;
using FluentAssertions;
using ZyncMaster.Engine;
using Xunit;

namespace ZyncMaster.Engine.Tests;

public sealed class WindowsAutoStartManagerTests
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ZyncMaster";

    private sealed class FakeRegistry : IRegistry
    {
        public readonly Dictionary<(string, string), string> Store = new();
        public string? GetValue(string subKeyPath, string valueName)
            => Store.TryGetValue((subKeyPath, valueName), out var v) ? v : null;
        public void SetValue(string subKeyPath, string valueName, string value)
            => Store[(subKeyPath, valueName)] = value;
        public void DeleteValue(string subKeyPath, string valueName)
            => Store.Remove((subKeyPath, valueName));
    }

    [Fact]
    public void Ctor_NullRegistry_Throws()
    {
        Action act = () => new WindowsAutoStartManager(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void IsEnabled_NoValue_ReturnsFalse()
    {
        var manager = new WindowsAutoStartManager(new FakeRegistry());
        manager.IsEnabled().Should().BeFalse();
    }

    [Fact]
    public void Enable_WritesQuotedExeAndArgsToRunKey()
    {
        var reg = new FakeRegistry();
        var manager = new WindowsAutoStartManager(reg);

        manager.Enable(@"C:\Program Files\ZyncMaster\ZyncMaster.App.exe", "--minimized");

        reg.Store.Should().ContainKey((RunKey, ValueName));
        reg.Store[(RunKey, ValueName)].Should().Be(@"""C:\Program Files\ZyncMaster\ZyncMaster.App.exe"" --minimized");
    }

    [Fact]
    public void Enable_NoArgs_WritesOnlyQuotedExe()
    {
        var reg = new FakeRegistry();
        var manager = new WindowsAutoStartManager(reg);

        manager.Enable(@"C:\app.exe", "");

        reg.Store[(RunKey, ValueName)].Should().Be(@"""C:\app.exe""");
    }

    [Fact]
    public void IsEnabled_AfterEnable_ReturnsTrue()
    {
        var reg = new FakeRegistry();
        var manager = new WindowsAutoStartManager(reg);

        manager.Enable(@"C:\app.exe", "-a");

        manager.IsEnabled().Should().BeTrue();
    }

    [Fact]
    public void Disable_RemovesValue()
    {
        var reg = new FakeRegistry();
        var manager = new WindowsAutoStartManager(reg);
        manager.Enable(@"C:\app.exe", "-a");

        manager.Disable();

        reg.Store.Should().NotContainKey((RunKey, ValueName));
        manager.IsEnabled().Should().BeFalse();
    }

    [Fact]
    public void Enable_NullExePath_Throws()
    {
        var manager = new WindowsAutoStartManager(new FakeRegistry());
        Action act = () => manager.Enable(null!, "-a");
        act.Should().Throw<ArgumentNullException>();
    }
}
