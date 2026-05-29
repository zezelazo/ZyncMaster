using System;

namespace ZyncMaster.Engine;

// Manages the HKCU "Run" entry that launches the host at login. The registry access
// goes through IRegistry so this class is unit-testable; WindowsRegistry is the real seam.
public sealed class WindowsAutoStartManager : IAutoStartManager
{
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "ZyncMaster";

    private readonly IRegistry _registry;

    public WindowsAutoStartManager(IRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public bool IsEnabled()
        => !string.IsNullOrEmpty(_registry.GetValue(RunKeyPath, ValueName));

    public void Enable(string exePath, string args)
    {
        if (exePath == null) throw new ArgumentNullException(nameof(exePath));

        var command = string.IsNullOrEmpty(args)
            ? $"\"{exePath}\""
            : $"\"{exePath}\" {args}";

        _registry.SetValue(RunKeyPath, ValueName, command);
    }

    public void Disable()
        => _registry.DeleteValue(RunKeyPath, ValueName);
}
