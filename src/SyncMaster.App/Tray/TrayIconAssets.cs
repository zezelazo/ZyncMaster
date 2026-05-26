using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SyncMaster.App.State;

namespace SyncMaster.App.Tray;

// Provides the tray icons for the four sync states.
//
// REQUIREMENT: there are four states (idle / syncing / error / paused). Ideally each
// would ship as a hand-tuned .ico asset embedded as AvaloniaResource. To keep the
// scaffold self-contained (no binary assets checked in), the icons are rendered at
// runtime from a small RenderTargetBitmap: a filled rounded square in a per-state
// colour with a glyph. Swap these for designed .ico/.png assets later by replacing
// the bodies of the For* methods with bitmaps loaded from avares:// resources.
public static class TrayIconAssets
{
    private const int Size = 32;

    // Per-state accent colours, matching the web UI aurora semantics:
    // idle = calm blue, syncing = cyan, error = red, paused = amber.
    private static readonly Color Idle    = Color.FromRgb(0x4F, 0x8C, 0xFF);
    private static readonly Color Syncing = Color.FromRgb(0x22, 0xD3, 0xEE);
    private static readonly Color Error   = Color.FromRgb(0xEF, 0x44, 0x44);
    private static readonly Color Paused  = Color.FromRgb(0xF5, 0x9E, 0x0B);

    public static WindowIcon For(SyncStatus status) => status switch
    {
        SyncStatus.Syncing => Build(Syncing),
        SyncStatus.Error   => Build(Error),
        SyncStatus.Paused  => Build(Paused),
        _                  => Build(Idle),
    };

    public static WindowIcon Default() => For(SyncStatus.Idle);

    private static WindowIcon Build(Color accent)
    {
        var pixelSize = new PixelSize(Size, Size);
        var dpi = new Vector(96, 96);
        using var rtb = new RenderTargetBitmap(pixelSize, dpi);

        using (var ctx = rtb.CreateDrawingContext())
        {
            var brush = new SolidColorBrush(accent);
            var rect = new Rect(2, 2, Size - 4, Size - 4);
            ctx.DrawRectangle(brush, null, rect, 8, 8);

            // A simple white centre dot so the glyph reads at tray size.
            var dot = new SolidColorBrush(Colors.White);
            ctx.DrawEllipse(dot, null, rect.Center, 5, 5);
        }

        using var stream = new MemoryStream();
        rtb.Save(stream);
        stream.Position = 0;
        return new WindowIcon(stream);
    }
}
