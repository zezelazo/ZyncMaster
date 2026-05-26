using System;
using System.Threading;
using Avalonia;

namespace SyncMaster.App;

// Entry point. A single-instance Mutex guard ensures only one tray-resident SyncMaster
// runs per user session; a second launch exits immediately rather than stacking a
// second tray icon.
internal static class Program
{
    private const string SingleInstanceMutexName = "Global\\SyncMaster.App.SingleInstance";

    [STAThread]
    public static int Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
            return 0; // Another instance already owns the tray — exit quietly.

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, shared by the entry point and the previewer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
