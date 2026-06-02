using System;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using ZyncMaster.App.State;
using ZyncMaster.App.Windows;

namespace ZyncMaster.App.Tray;

// Owns the system tray icon and its context menu. The app is tray-resident: there is
// no MainWindow set on the lifetime, so closing the window only hides it (see
// MainWindow.OnClosing) and the process keeps running behind this tray icon.
//
// SetStatus swaps the icon among the four states and updates the menu header text so
// the user can see the current state without opening the panel.
public sealed class TrayController : IDisposable
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly Func<MainWindow> _windowFactory;

    private TrayIcon? _tray;
    private NativeMenuItem? _statusHeader;
    private NativeMenuItem? _pauseItem;
    private MainWindow? _window;
    private SyncStatus _status = SyncStatus.Idle;
    private bool _paused;

    // Raised when the user clicks "Sync now" in the tray menu.
    public event Action? SyncNowRequested;

    // Raised when the user toggles "Pause auto-sync"; the bool is the new paused state.
    public event Action<bool>? PauseToggled;

    // Raised when the user clicks "Open web panel" (opens the UI in a browser).
    public event Action? OpenWebPanelRequested;

    public TrayController(IClassicDesktopStyleApplicationLifetime desktop, Func<MainWindow> windowFactory)
    {
        _desktop = desktop ?? throw new ArgumentNullException(nameof(desktop));
        _windowFactory = windowFactory ?? throw new ArgumentNullException(nameof(windowFactory));
    }

    public void Show()
    {
        _statusHeader = new NativeMenuItem(StatusHeaderText()) { IsEnabled = false };

        var openItem = new NativeMenuItem("Open Zync Master");
        openItem.Click += (_, _) => OpenWindow();

        var syncItem = new NativeMenuItem("Sync now");
        syncItem.Click += (_, _) => SyncNowRequested?.Invoke();

        _pauseItem = new NativeMenuItem(PauseItemText());
        _pauseItem.Click += (_, _) => TogglePause();

        var quitItem = new NativeMenuItem("Quit");
        quitItem.Click += (_, _) =>
        {
            // Real exit. Shutdown() forces teardown (it ignores the OnClosing cancel that the
            // hide-to-tray guard raises), so the app closes regardless. AllowClose() is belt-
            // and-suspenders in case the exit path is ever changed to the cancel-respecting
            // TryShutdown(); resolve the single window via the factory so it applies even when
            // the tray's "Open" was never used (e.g. the window came up via startup auto-show).
            (_window ??= _windowFactory()).AllowClose();
            _desktop.Shutdown();
        };

        var menu = new NativeMenu
        {
            Items =
            {
                _statusHeader,
                new NativeMenuItemSeparator(),
                openItem,
                syncItem,
                _pauseItem,
                new NativeMenuItemSeparator(),
                quitItem,
            },
        };

        _tray = new TrayIcon
        {
            Icon = TrayIconAssets.For(_status),
            ToolTipText = "Zync Master",
            Menu = menu,
            IsVisible = true,
        };

        // Left-click / double-click activation opens the window.
        _tray.Clicked += (_, _) => OpenWindow();
    }

    // Swaps the tray icon for the given state and refreshes the menu header text.
    public void SetStatus(SyncStatus status)
    {
        _status = status;
        _paused = status == SyncStatus.Paused;
        if (_tray != null)
            _tray.Icon = TrayIconAssets.For(status);
        if (_statusHeader != null)
            _statusHeader.Header = StatusHeaderText();
        if (_pauseItem != null)
            _pauseItem.Header = PauseItemText();
    }

    private void OpenWindow()
    {
        // Defer onto the dispatcher: creating/showing the window (which mounts the native
        // WebView2 control) directly inside the tray icon's Win32 callback re-enters the
        // message pump and crashes the process (0xc000041d). Posting runs it on a clean turn.
        Dispatcher.UIThread.Post(() =>
        {
            // The factory returns the single shared window (created + wired on first use),
            // so this reuses the startup-shown window rather than spawning a second one.
            _window ??= _windowFactory();
            _window.ShowToFront();
        });
    }

    private void TogglePause()
    {
        _paused = !_paused;
        if (_pauseItem != null)
            _pauseItem.Header = PauseItemText();
        PauseToggled?.Invoke(_paused);
    }

    private string PauseItemText() => TrayStatusText.PauseItem(_paused);

    private string StatusHeaderText() => TrayStatusText.Header(_status);

    public void Dispose()
    {
        if (_tray != null)
        {
            _tray.IsVisible = false;
            _tray.Dispose();
            _tray = null;
        }
    }
}
