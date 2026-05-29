using System.Diagnostics;

namespace ZyncMaster.Engine;

// Opens a URL in the user's default browser. This is an untested process boundary
// (like CalExportRunner / OutlookCalendarService): it shells out to the shell to
// resolve the default handler. It is consumed through IBrowserLauncher so
// PairingService can be unit-tested with a fake launcher.
public sealed class DefaultBrowserLauncher : IBrowserLauncher
{
    public void Open(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
