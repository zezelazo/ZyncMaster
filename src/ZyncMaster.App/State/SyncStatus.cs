namespace ZyncMaster.App.State;

// The four tray/visual states the app can be in. Drives the tray icon swap and the
// dashboard orb. Idle = paired and waiting for the next cycle; Syncing = a cycle is
// running; Error = the last cycle failed; Paused = auto-sync is paused by the user.
public enum SyncStatus
{
    Idle,
    Syncing,
    Error,
    Paused,
}
