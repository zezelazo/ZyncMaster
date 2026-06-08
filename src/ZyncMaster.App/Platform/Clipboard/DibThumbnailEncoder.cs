using System;
using System.IO;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Media.Imaging;
using ZyncMaster.Engine;

namespace ZyncMaster.App.Platform.Clipboard;

// Turns a clipboard CF_DIB blob into a small downscaled PNG thumbnail for the history viewer preview.
// The DIB is first wrapped into a decodable BMP (Engine DibImage.DibToBmp prepends the 14-byte file
// header), then decoded with Avalonia's Skia-backed Bitmap, downscaled so the longest side is ~320px
// (aspect preserved, never upscaled), and re-encoded as PNG.
//
// Best-effort by contract: ANY failure (malformed DIB, codec error, no Skia surface) returns null so
// the caller simply ships no thumbnail. It must never throw out of the capture path.
//
// Untested infra (Avalonia/Skia decode + encode); the pure header math it relies on lives in the
// Engine (DibImage) and IS unit-tested.
[SupportedOSPlatform("windows")]
internal static class DibThumbnailEncoder
{
    // Longest-side target for the thumbnail. Big enough to read in the viewer tile, small enough to keep
    // the per-item payload tiny on the wire and in storage.
    private const int MaxLongestSide = 320;

    public static byte[]? TryCreatePngThumbnail(byte[]? dib)
    {
        try
        {
            var bmpBytes = DibImage.DibToBmp(dib);
            if (bmpBytes is null)
                return null;

            using var bmpStream = new MemoryStream(bmpBytes, writable: false);
            using var source = new Bitmap(bmpStream);

            var (w, h) = ScaledSize(source.PixelSize.Width, source.PixelSize.Height);
            if (w <= 0 || h <= 0)
                return null;

            using var thumb = source.CreateScaledBitmap(new PixelSize(w, h));
            using var pngStream = new MemoryStream();
            thumb.Save(pngStream); // Avalonia Bitmap.Save writes PNG
            return pngStream.ToArray();
        }
        catch
        {
            // Best-effort: a bad DIB / codec failure / missing Skia surface yields no thumbnail.
            return null;
        }
    }

    // Computes the target pixel size: scale so the longest side == MaxLongestSide, preserving aspect,
    // and never enlarge a smaller image. Returns at least 1px on each axis for a valid tiny source.
    internal static (int width, int height) ScaledSize(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return (0, 0);

        var longest = Math.Max(width, height);
        if (longest <= MaxLongestSide)
            return (width, height); // already small enough — do not upscale.

        var scale = (double)MaxLongestSide / longest;
        var w = Math.Max(1, (int)Math.Round(width * scale));
        var h = Math.Max(1, (int)Math.Round(height * scale));
        return (w, h);
    }
}
