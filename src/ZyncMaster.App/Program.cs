using System;
using System.Threading;
using Avalonia;

namespace ZyncMaster.App;

// Entry point. A single-instance guard ensures only one tray-resident ZyncMaster runs per
// user session. A second launch does NOT stack a second tray icon: it signals the running
// instance (via a named EventWaitHandle) to surface its window, then exits quietly. The
// first instance owns the mutex for its whole lifetime and listens on that signal.
internal static class Program
{
    // Per-session (Local\) names: the app is per-user/per-session, not machine-global, so a
    // Local\ scope is correct and avoids the Global\ ACL pitfalls under fast user switching.
    private const string SingleInstanceMutexName = "Local\\ZyncMaster.App.SingleInstance";
    private const string ShowWindowEventName = "Local\\ZyncMaster.App.ShowWindow";

    // Signalled by a second launch to ask the running instance to bring its window forward.
    // Exposed so App can register a listener once its window/dispatcher are available.
    internal static EventWaitHandle? ShowWindowSignal { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            // Another instance already owns the tray. Tell it to show its window, then exit.
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out var existing))
                    using (existing) existing.Set();
            }
            catch
            {
                // Best effort: even if the signal can't be raised, we must not start a 2nd
                // instance. The user can still open the running app from its tray icon.
            }
            return 0;
        }

        // First (owning) instance: create the show-window signal so later launches can poke it.
        ShowWindowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            ShowWindowSignal.Dispose();
            ShowWindowSignal = null;
        }
    }

    // Avalonia configuration, shared by the entry point and the previewer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
