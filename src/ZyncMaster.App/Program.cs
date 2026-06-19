using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using ZyncMaster.Core;

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

    // Explicit Application User Model ID. Windows groups taskbar buttons and resolves the
    // taskbar / Alt-Tab icon by the process AUMID; when it is left unset the shell derives a
    // volatile one from the launching process, which under a debugger launch (and with the
    // embedded WebView2 child window) lands on the generic shell icon instead of the window
    // icon. Pinning a stable AUMID before any window is created makes the shell key this app's
    // taskbar entry to its own window/exe icon consistently across launch contexts.
    private const string AppUserModelId = "DevLabPe.ZyncMaster.App";

    // Signalled by a second launch to ask the running instance to bring its window forward.
    // Exposed so App can register a listener once its window/dispatcher are available.
    internal static EventWaitHandle? ShowWindowSignal { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // Last-resort crash logging. An exception that escapes a background thread, or an un-awaited
        // faulted Task, would otherwise tear the process down with no trace — the class of failure
        // that left users with a vanished tray app and an empty log. Hook the two process-level
        // escalation points so a crash lands in the same logs dir the rest of the app writes to,
        // for post-mortem diagnosis. Registered FIRST so it covers startup faults too.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrash("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved(); // stop the un-awaited faulted Task from escalating to a process kill
        };

        // Must run before any window is shown so the shell associates the taskbar entry with
        // this app's icon rather than a derived generic one. Best effort: only Windows 7+ has
        // the API, and a failure here must never block startup.
        if (OperatingSystem.IsWindows())
        {
            try { SetCurrentProcessExplicitAppUserModelID(AppUserModelId); }
            catch { /* Older shell or restricted host: fall back to shell default behaviour. */ }
        }

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

    // Self-contained, never-throws crash writer. It runs at the worst possible moment (the process is
    // dying), so it must NOT depend on the composed IAppLogger / DI — it writes straight to the log
    // directory. Appends to a dedicated crash.log so a post-mortem is one file away. Any failure here
    // is swallowed: the app is already going down and the crash handler must never make it worse.
    private static void WriteCrash(string source, Exception? ex)
    {
        try
        {
            var dir = DailyFileLogger.DefaultLogDirectory();
            Directory.CreateDirectory(dir);
            var line = $"{DateTimeOffset.UtcNow:O} [CRASH] {source}: {ex?.ToString() ?? "(no exception object)"}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(dir, "crash.log"), line);
        }
        catch { /* dying anyway — never throw from the crash handler */ }
    }

    // Avalonia configuration, shared by the entry point and the previewer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    // shell32: pins the taskbar grouping / icon identity for this process.
    [DllImport("shell32.dll", PreserveSig = false)]
    private static extern void SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string appId);
}
