using System;
using Avalonia.Controls;
using Avalonia.Platform;
using ZyncMaster.App.State;

namespace ZyncMaster.App.Tray;

// Supplies the tray icon. The brand mark (Assets/icon.png) is used for every state;
// the current sync state is surfaced in the tray menu header text instead of recolouring
// the icon, so the app reads as one consistent mark in the tray / menu bar.
public static class TrayIconAssets
{
    private static WindowIcon? _icon;

    public static WindowIcon For(SyncStatus status) => Brand();

    public static WindowIcon Default() => Brand();

    private static WindowIcon Brand()
    {
        if (_icon != null) return _icon;
        using var stream = AssetLoader.Open(new Uri("avares://ZyncMaster.App/Assets/icon.png"));
        _icon = new WindowIcon(stream);
        return _icon;
    }
}
