using System;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using SyncMaster.App.State;
using SyncMaster.App.Windows;

namespace SyncMaster.App.Tray;

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

        var openItem = new NativeMenuItem("Open SyncMaster");
        openItem.Click += (_, _) => OpenWindow();

        var syncItem = new NativeMenuItem("Sync now");
        syncItem.Click += (_, _) => SyncNowRequested?.Invoke();

        _pauseItem = new NativeMenuItem(PauseItemText());
        _pauseItem.Click += (_, _) => TogglePause();

        var configItem = new NativeMenuItem("Configuration…");
        configItem.Click += (_, _) => OpenWindow();

        var webItem = new NativeMenuItem("Open web panel");
        webItem.Click += (_, _) => OpenWebPanelRequested?.Invoke();

        var quitItem = new NativeMenuItem("Quit");
        quitItem.Click += (_, _) => _desktop.Shutdown();

        var menu = new NativeMenu
        {
            Items =
            {
                _statusHeader,
                new NativeMenuItemSeparator(),
                openItem,
                syncItem,
                _pauseItem,
                configItem,
                webItem,
                new NativeMenuItemSeparator(),
                quitItem,
            },
        };

        _tray = new TrayIcon
        {
            Icon = TrayIconAssets.For(_status),
            ToolTipText = "SyncMaster",
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
        _window ??= _windowFactory();
        _window.Show();
        _window.Activate();
    }

    private void TogglePause()
    {
        _paused = !_paused;
        if (_pauseItem != null)
            _pauseItem.Header = PauseItemText();
        PauseToggled?.Invoke(_paused);
    }

    private string PauseItemText() => _paused ? "Resume auto-sync" : "Pause auto-sync";

    private string StatusHeaderText() => _status switch
    {
        SyncStatus.Syncing => "SyncMaster — Syncing…",
        SyncStatus.Error   => "SyncMaster — Error",
        SyncStatus.Paused  => "SyncMaster — Paused",
        _                  => "SyncMaster — Idle",
    };

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
