using System;

namespace ZyncMaster.Engine;

// Pure, platform-agnostic helper for turning a clipboard CF_DIB blob into a standalone, decodable BMP
// file by prepending the 14-byte BITMAPFILEHEADER that CF_DIB omits. CF_DIB is exactly the contents of
// a .bmp file MINUS that header: it starts at the BITMAPINFOHEADER, optionally followed by a colour
// table / BI_BITFIELDS masks, then the pixel array. Prepending a correct header (with the right pixel
// data offset) yields bytes any BMP decoder (Avalonia/Skia/WIC/System.Drawing) can open.
//
// No imaging dependency here on purpose: only the header math lives in the Engine so it is unit-testable
// without a codec. The actual decode + downscale + PNG encode is done by a Windows-gated App helper.
public static class DibImage
{
    private const int BitmapFileHeaderSize = 14;          // 'BM' + fileSize + 2 reserved + pixelOffset
    private const int MinInfoHeaderSize = 40;             // BITMAPINFOHEADER (biSize..biClrImportant)

    // BITMAPINFOHEADER field offsets (relative to the start of the DIB).
    private const int OffBiSize = 0;
    private const int OffBiBitCount = 14;
    private const int OffBiCompression = 16;
    private const int OffBiClrUsed = 32;

    private const int BiBitfields = 3;                    // biCompression == BI_BITFIELDS

    // Builds a BMP byte[] from a CF_DIB blob, or null when the DIB is too short / malformed to read a
    // header from. Never throws on bad input.
    public static byte[]? DibToBmp(byte[]? dib)
    {
        if (dib is null || dib.Length < MinInfoHeaderSize)
            return null;

        var biSize = ReadInt32(dib, OffBiSize);
        // A sane DIB header is at least a BITMAPINFOHEADER and fits inside the blob. Reject anything else.
        if (biSize < MinInfoHeaderSize || biSize > dib.Length)
            return null;

        var biBitCount = ReadUInt16(dib, OffBiBitCount);
        var biCompression = ReadInt32(dib, OffBiCompression);
        var biClrUsed = ReadInt32(dib, OffBiClrUsed);

        var paletteBytes = ComputePaletteBytes(biBitCount, biCompression, biClrUsed);

        // pixelOffset = 14 (file header) + DIB header size + colour table / bitfield masks.
        long pixelOffset = (long)BitmapFileHeaderSize + biSize + paletteBytes;
        if (pixelOffset >= dib.Length)
            return null; // the computed pixels would start at or past the end of the blob — malformed (no pixel data).

        long fileSize = (long)BitmapFileHeaderSize + dib.Length;
        if (fileSize > int.MaxValue || pixelOffset > int.MaxValue)
            return null;

        var bmp = new byte[BitmapFileHeaderSize + dib.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        WriteInt32(bmp, 2, (int)fileSize);   // bfSize
        WriteInt32(bmp, 6, 0);               // bfReserved1 + bfReserved2
        WriteInt32(bmp, 10, (int)pixelOffset); // bfOffBits

        Buffer.BlockCopy(dib, 0, bmp, BitmapFileHeaderSize, dib.Length);
        return bmp;
    }

    // Bytes occupied by the colour table (<= 8 bpp) or the BI_BITFIELDS masks, which sit between the
    // info header and the pixels and therefore shift bfOffBits.
    //   * biClrUsed explicit -> that many 4-byte RGBQUAD entries (wins for any bit depth);
    //   * else <= 8 bpp -> a full 2^biBitCount-entry palette;
    //   * else (>= 16 bpp) no palette, but BI_BITFIELDS adds three 4-byte channel masks.
    private static long ComputePaletteBytes(int biBitCount, int biCompression, int biClrUsed)
    {
        if (biClrUsed > 0)
            return (long)biClrUsed * 4;

        if (biBitCount is > 0 and <= 8)
            return (1L << biBitCount) * 4;

        if (biCompression == BiBitfields)
            return 3L * 4; // red/green/blue masks

        return 0;
    }

    private static int ReadInt32(byte[] b, int offset)
        => b[offset] | (b[offset + 1] << 8) | (b[offset + 2] << 16) | (b[offset + 3] << 24);

    private static ushort ReadUInt16(byte[] b, int offset)
        => (ushort)(b[offset] | (b[offset + 1] << 8));

    private static void WriteInt32(byte[] b, int offset, int value)
    {
        b[offset] = (byte)(value & 0xFF);
        b[offset + 1] = (byte)((value >> 8) & 0xFF);
        b[offset + 2] = (byte)((value >> 16) & 0xFF);
        b[offset + 3] = (byte)((value >> 24) & 0xFF);
    }
}
