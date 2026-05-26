using SyncMaster.App.State;

namespace SyncMaster.App.Tray;

// Pure mapping from a SyncStatus to the tray menu header text and the pause/resume
// item label. Kept separate from TrayController (which owns Avalonia TrayIcon/NativeMenu
// objects that can't be constructed headlessly) so the status-text reactivity is
// unit-testable on its own.
public static class TrayStatusText
{
    public static string Header(SyncStatus status) => status switch
    {
        SyncStatus.Syncing => "SyncMaster — Syncing…",
        SyncStatus.Error   => "SyncMaster — Error",
        SyncStatus.Paused  => "SyncMaster — Paused",
        _                  => "SyncMaster — Idle",
    };

    public static string PauseItem(bool paused) => paused ? "Resume auto-sync" : "Pause auto-sync";
}
