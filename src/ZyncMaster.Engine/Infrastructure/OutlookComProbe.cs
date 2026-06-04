using System;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ZyncMaster.Engine;

// Real IOutlookComProbe. Detects Outlook Classic by looking for its COM ProgID under
// HKEY_CLASSES_ROOT — the ProgID lives in HKCR, not HKCU, so the existing WindowsRegistry
// (HKCU-only, for login auto-start) cannot be reused here. This is a thin, untested OS wrapper
// like WindowsRegistry: a cheap registry read, never instantiating the COM server (which would
// launch Outlook). Off Windows or on any error it reports "not available".
public sealed class WindowsOutlookComProbe : IOutlookComProbe
{
    public bool IsAvailable()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            return ProbeWindows();
        }
        catch
        {
            // A registry-access failure (locked-down ACL, redirection) is treated as "not
            // available" rather than crashing the capability probe.
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool ProbeWindows()
    {
        // Outlook.Application\CLSID is the canonical, cheapest signal that the COM server is
        // registered; fall back to the bare Outlook.Application ProgID key.
        using (var clsid = Registry.ClassesRoot.OpenSubKey(@"Outlook.Application\CLSID"))
        {
            if (clsid is not null)
                return true;
        }

        using var progId = Registry.ClassesRoot.OpenSubKey("Outlook.Application");
        return progId is not null;
    }
}
