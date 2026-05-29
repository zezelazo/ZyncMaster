using System;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ZyncMaster.Engine;

// Real IRegistry over HKEY_CURRENT_USER. Windows-only; every member guards on
// OperatingSystem.IsWindows() so the type can be referenced from cross-platform code.
public sealed class WindowsRegistry : IRegistry
{
    public string? GetValue(string subKeyPath, string valueName)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        return GetValueWindows(subKeyPath, valueName);
    }

    public void SetValue(string subKeyPath, string valueName, string value)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Registry auto-start is only supported on Windows.");
        SetValueWindows(subKeyPath, valueName, value);
    }

    public void DeleteValue(string subKeyPath, string valueName)
    {
        if (!OperatingSystem.IsWindows())
            return;
        DeleteValueWindows(subKeyPath, valueName);
    }

    [SupportedOSPlatform("windows")]
    private static string? GetValueWindows(string subKeyPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKeyPath);
        return key?.GetValue(valueName) as string;
    }

    [SupportedOSPlatform("windows")]
    private static void SetValueWindows(string subKeyPath, string valueName, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKeyPath, writable: true);
        key.SetValue(valueName, value, RegistryValueKind.String);
    }

    [SupportedOSPlatform("windows")]
    private static void DeleteValueWindows(string subKeyPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
