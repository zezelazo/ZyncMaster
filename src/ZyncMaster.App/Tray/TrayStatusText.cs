using ZyncMaster.App.State;

namespace ZyncMaster.App.Tray;

// Pure mapping from a SyncStatus to the tray menu header text and the pause/resume
// item label. Kept separate from TrayController (which owns Avalonia TrayIcon/NativeMenu
// objects that can't be constructed headlessly) so the status-text reactivity is
// unit-testable on its own.
public static class TrayStatusText
{
    public static string Header(SyncStatus status) => status switch
    {
        SyncStatus.Syncing => "ZyncMaster — Syncing…",
        SyncStatus.Error   => "ZyncMaster — Error",
        SyncStatus.Paused  => "ZyncMaster — Paused",
        _                  => "ZyncMaster — Idle",
    };

    public static string PauseItem(bool paused) => paused ? "Resume auto-sync" : "Pause auto-sync";
}
