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

    // Reads the raw CF_DIB blob (BITMAPINFOHEADER + pixel data). The transport carries these bytes
    // verbatim; the receiving sink writes them back as CF_DIB.
    public static byte[]? TryReadImageDib()
    {
        if (!Win32.IsClipboardFormatAvailable(Win32.CF_DIB))
            return null;
        if (!TryOpen(IntPtr.Zero))
            return null;
        try
        {
            var handle = Win32.GetClipboardData(Win32.CF_DIB);
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
