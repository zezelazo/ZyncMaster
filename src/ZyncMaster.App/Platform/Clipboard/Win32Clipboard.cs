using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace ZyncMaster.App.Platform.Clipboard;

// Low-level read/write of the Windows clipboard via the Win32 API. Text rides CF_UNICODETEXT; images
// ride CF_DIB (the device-independent bitmap blob), which round-trips losslessly without pulling in
// System.Drawing or an image codec. All operations are best-effort: every method swallows failures
// and returns null / false rather than throwing, because the OS clipboard is shared global state that
// another process can lock or hand us malformed data at any instant.
//
// Untested process boundary (Win32 clipboard).
[SupportedOSPlatform("windows")]
internal static class Win32Clipboard
{
    // Bounded retry: the clipboard is a single global resource and another process may briefly hold
    // it open. A few quick attempts avoids spurious empty reads/writes without blocking the caller.
    private const int OpenRetries = 5;

    public static string? TryReadText()
    {
        if (!Win32.IsClipboardFormatAvailable(Win32.CF_UNICODETEXT))
            return null;
        if (!TryOpen(IntPtr.Zero))
            return null;
        try
        {
            var handle = Win32.GetClipboardData(Win32.CF_UNICODETEXT);
            if (handle == IntPtr.Zero)
                return null;

            var ptr = Win32.GlobalLock(handle);
            if (ptr == IntPtr.Zero)
                return null;
            try
            {
                return Marshal.PtrToStringUni(ptr);
            }
            finally
            {
                Win32.GlobalUnlock(handle);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }

    // Reads an image off the clipboard as a CF_DIB blob (BITMAPINFOHEADER + pixel data). The transport
    // carries these bytes verbatim; the receiving sink writes them back as CF_DIB.
    //
    // Order of attempts: CF_DIB directly (the common case), then a best-effort synthesis from CF_BITMAP
    // via GetDIBits. The second path matters because some apps publish only a bitmap HANDLE (CF_BITMAP)
    // and rely on the OS to synthesize CF_DIB lazily — but when the source process dies or the format is
    // not auto-synthesized, IsClipboardFormatAvailable(CF_DIB) is false and a CF_DIB-only read returns
    // null, silently dropping the image. Converting CF_BITMAP ourselves recovers those copies.
    public static byte[]? TryReadImageDib()
    {
        if (Win32.IsClipboardFormatAvailable(Win32.CF_DIB))
        {
            var dib = ReadGlobalBlob(Win32.CF_DIB);
            if (dib is { Length: > 0 })
                return dib;
        }

        if (Win32.IsClipboardFormatAvailable(Win32.CF_DIBV5))
        {
            // NOTE: a CF_DIBV5 blob keeps its 124-byte BITMAPV5HEADER, and the sink later writes the
            // returned bytes back under the CF_DIB format id. A strict CF_DIB consumer expects a 40-byte
            // BITMAPINFOHEADER, so a V5 header under the CF_DIB label is technically loose. It decodes in
            // practice because every consumer in this pipeline (DibImage, the thumbnail encoder) reads
            // biSize first and skips the header dynamically. If a strict external consumer ever needs a
            // 40-byte header, normalize the V5 header here or write it back as CF_DIBV5 on the sink side.
            var dibv5 = ReadGlobalBlob(Win32.CF_DIBV5);
            if (dibv5 is { Length: > 0 })
                return dibv5;
        }

        if (Win32.IsClipboardFormatAvailable(Win32.CF_BITMAP))
        {
            var fromBitmap = TryReadBitmapAsDib();
            if (fromBitmap is { Length: > 0 })
                return fromBitmap;
        }

        return null;
    }

    // Copies a HGLOBAL-backed clipboard format (CF_DIB / CF_DIBV5) into a managed byte[]. Best-effort.
    private static byte[]? ReadGlobalBlob(uint format)
    {
        if (!TryOpen(IntPtr.Zero))
            return null;
        try
        {
            var handle = Win32.GetClipboardData(format);
            if (handle == IntPtr.Zero)
                return null;

            var size = (long)Win32.GlobalSize(handle);
            if (size <= 0)
                return null;

            var ptr = Win32.GlobalLock(handle);
            if (ptr == IntPtr.Zero)
                return null;
            try
            {
                var buffer = new byte[size];
                Marshal.Copy(ptr, buffer, 0, (int)size);
                return buffer;
            }
            finally
            {
                Win32.GlobalUnlock(handle);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }

    // Synthesizes a packed DIB (BITMAPINFOHEADER + 32bpp BI_RGB pixels) from a CF_BITMAP handle using
    // GetDIBits. Returns the same byte layout a CF_DIB carries, so the rest of the pipeline (DibImage,
    // the thumbnail encoder, the sink writing it back as CF_DIB) handles it unchanged. Best-effort: any
    // failure (no DC, GetDIBits error, oversize) returns null rather than throwing.
    private static byte[]? TryReadBitmapAsDib()
    {
        if (!TryOpen(IntPtr.Zero))
            return null;
        try
        {
            var hbmp = Win32.GetClipboardData(Win32.CF_BITMAP);
            if (hbmp == IntPtr.Zero)
                return null;

            var bmp = default(Win32.BITMAP);
            if (Win32.GetObject(hbmp, Marshal.SizeOf<Win32.BITMAP>(), ref bmp) == 0)
                return null;
            if (bmp.bmWidth <= 0 || bmp.bmHeight == 0)
                return null;

            var hdc = Win32.GetDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero)
                return null;
            try
            {
                // 32bpp bottom-up (positive biHeight) BI_RGB DIB: no palette, no compression, trivially
                // decodable. Positive biHeight is the standard CF_DIB bottom-up row order the sink writes
                // back and DibImage expects, so the synthesized blob matches a real CF_DIB exactly.
                var header = new Win32.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                    biWidth = bmp.bmWidth,
                    biHeight = bmp.bmHeight,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = Win32.BI_RGB,
                };

                // First call (null buffer) fills biSizeImage. Fall back to the computed stride*height if
                // the driver leaves it zero (allowed for BI_RGB).
                if (Win32.GetDIBits(hdc, hbmp, 0, (uint)Math.Abs(bmp.bmHeight), null, ref header, Win32.DIB_RGB_COLORS) == 0)
                    return null;

                var stride = ((bmp.bmWidth * 32 + 31) / 32) * 4;
                var imageSize = header.biSizeImage != 0 ? (int)header.biSizeImage : stride * Math.Abs(bmp.bmHeight);
                if (imageSize <= 0)
                    return null;

                var headerSize = (int)header.biSize;
                var dib = new byte[headerSize + imageSize];

                // Read the pixels into the tail of the blob (after the header).
                var pixels = new byte[imageSize];
                if (Win32.GetDIBits(hdc, hbmp, 0, (uint)Math.Abs(bmp.bmHeight), pixels, ref header, Win32.DIB_RGB_COLORS) == 0)
                    return null;

                // GetDIBits may have rewritten the header (e.g. biSizeImage); serialize the final header.
                MarshalHeader(header, dib);
                Buffer.BlockCopy(pixels, 0, dib, headerSize, imageSize);
                return dib;
            }
            finally
            {
                Win32.ReleaseDC(IntPtr.Zero, hdc);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }

    private static void MarshalHeader(Win32.BITMAPINFOHEADER header, byte[] destination)
    {
        var size = (int)header.biSize;
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(header, ptr, false);
            Marshal.Copy(ptr, destination, 0, size);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    // A short human-readable list of the formats currently on the clipboard, for the "nothing
    // extractable" diagnostic log. Best-effort: returns "unknown" if the clipboard can't be opened.
    public static string DescribeAvailableFormats()
    {
        if (!TryOpen(IntPtr.Zero))
            return "unknown";
        try
        {
            var names = new System.Collections.Generic.List<string>();
            uint format = 0;
            var name = new System.Text.StringBuilder(128);
            while ((format = Win32.EnumClipboardFormats(format)) != 0)
            {
                name.Clear();
                var len = Win32.GetClipboardFormatName(format, name, name.Capacity);
                names.Add(len > 0 ? name.ToString() : FormatId(format));
                if (names.Count >= 16)
                    break;
            }
            return names.Count == 0 ? "none" : string.Join(", ", names);
        }
        catch
        {
            return "unknown";
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }

    // Maps the well-known standard format ids to readable names; anything else shows its numeric id.
    private static string FormatId(uint format) => format switch
    {
        Win32.CF_UNICODETEXT => "CF_UNICODETEXT",
        Win32.CF_BITMAP => "CF_BITMAP",
        Win32.CF_DIB => "CF_DIB",
        Win32.CF_DIBV5 => "CF_DIBV5",
        _ => $"0x{format:X}",
    };

    public static bool TryWriteText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!TryOpen(IntPtr.Zero))
            return false;
        try
        {
            Win32.EmptyClipboard();
            var bytes = (text.Length + 1) * 2; // UTF-16 + null terminator
            var hMem = Win32.GlobalAlloc(Win32.GMEM_MOVEABLE, (UIntPtr)bytes);
            if (hMem == IntPtr.Zero)
                return false;

            var ptr = Win32.GlobalLock(hMem);
            if (ptr == IntPtr.Zero)
            {
                Win32.GlobalFree(hMem);
                return false;
            }
            try
            {
                Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
                Marshal.WriteInt16(ptr, text.Length * 2, 0); // null terminator
            }
            finally
            {
                Win32.GlobalUnlock(hMem);
            }

            if (Win32.SetClipboardData(Win32.CF_UNICODETEXT, hMem) == IntPtr.Zero)
            {
                // Ownership did NOT transfer to the system; free our block.
                Win32.GlobalFree(hMem);
                return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }

    public static bool TryWriteImageDib(byte[] dib)
    {
        ArgumentNullException.ThrowIfNull(dib);
        if (dib.Length == 0)
            return false;
        if (!TryOpen(IntPtr.Zero))
            return false;
        try
        {
            Win32.EmptyClipboard();
            var hMem = Win32.GlobalAlloc(Win32.GMEM_MOVEABLE, (UIntPtr)dib.Length);
            if (hMem == IntPtr.Zero)
                return false;

            var ptr = Win32.GlobalLock(hMem);
            if (ptr == IntPtr.Zero)
            {
                Win32.GlobalFree(hMem);
                return false;
            }
            try
            {
                Marshal.Copy(dib, 0, ptr, dib.Length);
            }
            finally
            {
                Win32.GlobalUnlock(hMem);
            }

            if (Win32.SetClipboardData(Win32.CF_DIB, hMem) == IntPtr.Zero)
            {
                Win32.GlobalFree(hMem);
                return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Win32.CloseClipboard();
        }
    }

    private static bool TryOpen(IntPtr owner)
    {
        for (var attempt = 0; attempt < OpenRetries; attempt++)
        {
            if (Win32.OpenClipboard(owner))
                return true;
            Thread.Sleep(15);
        }
        return false;
    }
}
