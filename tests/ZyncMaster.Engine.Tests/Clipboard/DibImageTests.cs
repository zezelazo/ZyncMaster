using System;
using FluentAssertions;
using ZyncMaster.Engine;
using Xunit;

namespace ZyncMaster.Engine.Tests.Clipboard;

// Header math for DibImage.DibToBmp: the prepended 14-byte BITMAPFILEHEADER must carry the 'BM'
// signature, the right total file size, and the correct pixel-data offset for the common DIB shapes
// (24bpp no palette, 8bpp 256-entry palette, BI_BITFIELDS masks, explicit biClrUsed). The actual
// pixel decode is untested infra (Avalonia/Skia).
public sealed class DibImageTests
{
    private const int InfoHeaderSize = 40;

    // Builds a minimal BITMAPINFOHEADER-only DIB (no pixels needed beyond the offset we assert) with the
    // given bit count / compression / clrUsed, padded so the computed pixel offset still lands inside.
    private static byte[] BuildDib(int biSize, ushort biBitCount, int biCompression, int biClrUsed, int extraPixelBytes = 16)
    {
        // Compute palette bytes the same way the helper does, so the blob is at least as long as the
        // pixel offset (otherwise DibToBmp returns null by design).
        long paletteBytes =
            biClrUsed > 0 ? (long)biClrUsed * 4
            : (biBitCount is > 0 and <= 8) ? (1L << biBitCount) * 4
            : (biCompression == 3) ? 12
            : 0;

        var len = (int)(biSize + paletteBytes + extraPixelBytes);
        var dib = new byte[len];
        WriteInt32(dib, 0, biSize);        // biSize
        WriteInt32(dib, 4, 8);             // biWidth
        WriteInt32(dib, 8, 8);             // biHeight
        WriteUInt16(dib, 12, 1);           // biPlanes
        WriteUInt16(dib, 14, biBitCount);  // biBitCount
        WriteInt32(dib, 16, biCompression);// biCompression
        WriteInt32(dib, 32, biClrUsed);    // biClrUsed
        return dib;
    }

    private static int ReadInt32(byte[] b, int o) =>
        b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);

    private static void WriteInt32(byte[] b, int o, int v)
    {
        b[o] = (byte)(v & 0xFF); b[o + 1] = (byte)((v >> 8) & 0xFF);
        b[o + 2] = (byte)((v >> 16) & 0xFF); b[o + 3] = (byte)((v >> 24) & 0xFF);
    }

    private static void WriteUInt16(byte[] b, int o, ushort v)
    {
        b[o] = (byte)(v & 0xFF); b[o + 1] = (byte)((v >> 8) & 0xFF);
    }

    [Fact]
    public void DibToBmp_24bpp_no_palette_has_BM_signature_and_offset_54()
    {
        var dib = BuildDib(InfoHeaderSize, biBitCount: 24, biCompression: 0, biClrUsed: 0);

        var bmp = DibImage.DibToBmp(dib);

        bmp.Should().NotBeNull();
        bmp![0].Should().Be((byte)'B');
        bmp[1].Should().Be((byte)'M');

        // 24bpp has no colour table: pixelOffset = 14 + 40 = 54.
        ReadInt32(bmp, 10).Should().Be(54);
        // bfSize = 14 + dib length.
        ReadInt32(bmp, 2).Should().Be(14 + dib.Length);
        // The DIB rides verbatim after the 14-byte header.
        bmp.Length.Should().Be(14 + dib.Length);
    }

    [Fact]
    public void DibToBmp_8bpp_256_palette_offset_accounts_for_palette()
    {
        // 8bpp with an implicit full palette (biClrUsed = 0 -> 2^8 = 256 entries * 4 bytes = 1024).
        var dib = BuildDib(InfoHeaderSize, biBitCount: 8, biCompression: 0, biClrUsed: 0);

        var bmp = DibImage.DibToBmp(dib);

        bmp.Should().NotBeNull();
        // pixelOffset = 14 + 40 + 256*4 = 1078.
        ReadInt32(bmp!, 10).Should().Be(14 + 40 + 256 * 4);
    }

    [Fact]
    public void DibToBmp_explicit_clrUsed_drives_palette_size()
    {
        // 8bpp but only 16 palette entries explicitly used -> 16*4 = 64 palette bytes.
        var dib = BuildDib(InfoHeaderSize, biBitCount: 8, biCompression: 0, biClrUsed: 16);

        var bmp = DibImage.DibToBmp(dib);

        bmp.Should().NotBeNull();
        ReadInt32(bmp!, 10).Should().Be(14 + 40 + 16 * 4);
    }

    [Fact]
    public void DibToBmp_bitfields_adds_three_masks_before_pixels()
    {
        // 32bpp BI_BITFIELDS (biCompression = 3): no palette, but 3 * 4-byte channel masks.
        var dib = BuildDib(InfoHeaderSize, biBitCount: 32, biCompression: 3, biClrUsed: 0);

        var bmp = DibImage.DibToBmp(dib);

        bmp.Should().NotBeNull();
        ReadInt32(bmp!, 10).Should().Be(14 + 40 + 12);
    }

    [Fact]
    public void DibToBmp_reserved_field_is_zero()
    {
        var dib = BuildDib(InfoHeaderSize, biBitCount: 24, biCompression: 0, biClrUsed: 0);

        var bmp = DibImage.DibToBmp(dib);

        ReadInt32(bmp!, 6).Should().Be(0); // bfReserved1 + bfReserved2
    }

    [Fact]
    public void DibToBmp_v5_header_124_bytes_offset_accounts_for_larger_header()
    {
        // CF_DIBV5 recovery keeps the 124-byte BITMAPV5HEADER, and the capture pipeline writes those
        // bytes back under the CF_DIB format id. DibToBmp must honour biSize dynamically, so a V5 header
        // shifts the pixel offset past the larger header instead of assuming a 40-byte BITMAPINFOHEADER.
        const int V5HeaderSize = 124;
        var dib = BuildDib(V5HeaderSize, biBitCount: 32, biCompression: 0, biClrUsed: 0);

        var bmp = DibImage.DibToBmp(dib);

        bmp.Should().NotBeNull();
        bmp![0].Should().Be((byte)'B');
        bmp[1].Should().Be((byte)'M');
        // 32bpp BI_RGB has no colour table: pixelOffset = 14 + 124 = 138.
        ReadInt32(bmp, 10).Should().Be(14 + V5HeaderSize);
        ReadInt32(bmp, 2).Should().Be(14 + dib.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[0])]
    public void DibToBmp_null_or_too_short_returns_null(byte[]? dib)
    {
        DibImage.DibToBmp(dib).Should().BeNull();
    }

    [Fact]
    public void DibToBmp_header_larger_than_blob_returns_null()
    {
        // biSize claims a 40-byte header but the blob is shorter than that -> malformed.
        var dib = new byte[20];
        WriteInt32(dib, 0, 40);
        DibImage.DibToBmp(dib).Should().BeNull();
    }

    [Fact]
    public void DibToBmp_pixel_offset_past_end_returns_null()
    {
        // 8bpp with a 256-entry palette needs 14+40+1024 bytes of offset, but the blob holds only the
        // header -> the pixels would start past the end -> null.
        var dib = new byte[InfoHeaderSize];
        WriteInt32(dib, 0, InfoHeaderSize);
        WriteUInt16(dib, 14, 8); // 8bpp
        DibImage.DibToBmp(dib).Should().BeNull();
    }
}
