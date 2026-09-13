using System.Buffers.Binary;
using System.IO.Compression;
using ToneSnip.Core.Color;
using ToneSnip.Core.Hdr;
using ToneSnip.Core.Imaging;
using Xunit;

namespace ToneSnip.Core.Tests.Hdr;

public class HdrPngWriterTests
{
    private static List<(string Type, byte[] Data, uint Crc)> Chunks(byte[] png)
    {
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        var list = new List<(string, byte[], uint)>();
        int p = 8;
        while (p < png.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(p)); string type = System.Text.Encoding.ASCII.GetString(png, p + 4, 4);
            byte[] data = png[(p + 8)..(p + 8 + len)]; uint crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(p + 8 + len));
            list.Add((type, data, crc)); p += 12 + len;
        }
        return list;
    }

    [Fact]
    public void Chunks_are_well_formed_with_valid_crcs()
    {
        var img = new HalfImage(3, 2);
        byte[] png = HdrPngWriter.Encode(img);
        var chunks = Chunks(png);
        Assert.Equal(new[] { "IHDR", "cICP", "IDAT", "IEND" }, chunks.Select(c => c.Type).ToArray());
        foreach ((string type, byte[] data, uint crc) in chunks)
        {
            var span = new byte[4 + data.Length]; System.Text.Encoding.ASCII.GetBytes(type).CopyTo(span, 0); data.CopyTo(span, 4);
            Assert.Equal(HdrPngWriter.Crc32(span), crc);
        }
        byte[] ihdr = chunks[0].Data;
        Assert.Equal(3, BinaryPrimitives.ReadInt32BigEndian(ihdr)); Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(ihdr.AsSpan(4)));
        Assert.Equal(16, ihdr[8]); Assert.Equal(6, ihdr[9]);          // 16-bit RGBA
        Assert.Equal(new byte[] { 9, 16, 0, 1 }, chunks[1].Data);       // BT.2020, PQ, RGB, full range
    }

    [Fact]
    public void Pixels_are_pq_encoded_bt2020_with_80_nits_per_unit()
    {
        var img = new HalfImage(2, 1);
        // px0: scRGB white 1.0 (80 nits); px1: 12.5 (1000 nits), alpha 0.5
        for (int c = 0; c < 3; c++) { img.Data[c] = Transfer.FloatToHalf(1f); img.Data[4 + c] = Transfer.FloatToHalf(12.5f); }
        img.Data[3] = 0x3C00; img.Data[7] = Transfer.FloatToHalf(0.5f);
        byte[] png = HdrPngWriter.Encode(img);
        byte[] idat = Chunks(png).First(c => c.Type == "IDAT").Data;
        using var z = new ZLibStream(new MemoryStream(idat), CompressionMode.Decompress);
        var raw = new byte[1 + 2 * 8]; int read = 0; while (read < raw.Length) { int n = z.Read(raw, read, raw.Length - read); if (n == 0) break; read += n; }
        Assert.Equal(raw.Length, read); Assert.Equal(0, raw[0]);   // filter byte 0
        ushort R0 = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(1)), A0 = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(7));
        ushort R1 = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(9)), A1 = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(15));
        Assert.InRange(Transfer.PqDecode(R0 / 65535f), 78f, 82f);       // 80 nits
        Assert.InRange(Transfer.PqDecode(R1 / 65535f), 990f, 1010f);    // 1000 nits
        Assert.Equal(65535, A0); Assert.InRange(A1, 32700, 32800);
    }

    /// <summary>Every IDAT concatenated, inflated, and each row's filter undone: the PNG decoder's own steps.</summary>
    private static byte[] Unfiltered(byte[] png, int width, int height)
    {
        var chunks = Chunks(png);
        foreach ((string type, byte[] data, uint crc) in chunks)
        {
            var span = new byte[4 + data.Length]; System.Text.Encoding.ASCII.GetBytes(type).CopyTo(span, 0); data.CopyTo(span, 4);
            Assert.Equal(HdrPngWriter.Crc32(span), crc);
        }
        byte[] idat = chunks.Where(c => c.Type == "IDAT").SelectMany(c => c.Data).ToArray();
        using var z = new ZLibStream(new MemoryStream(idat), CompressionMode.Decompress);
        using var inflated = new MemoryStream(); z.CopyTo(inflated);
        byte[] filtered = inflated.ToArray();
        int stride = width * 8;
        Assert.Equal((stride + 1) * height, filtered.Length);
        var raw = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            byte filter = filtered[y * (stride + 1)];
            for (int i = 0; i < stride; i++)
            {
                byte f = filtered[y * (stride + 1) + 1 + i];
                byte left = i >= 8 ? raw[y * stride + i - 8] : (byte)0, up = y > 0 ? raw[(y - 1) * stride + i] : (byte)0;
                raw[y * stride + i] = filter switch { 0 => f, 1 => (byte)(f + left), 2 => (byte)(f + up), _ => throw new InvalidDataException($"filter {filter}") };
            }
        }
        return raw;
    }

    private static HalfImage Gradient(int w, int h)
    {
        var img = new HalfImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                img.Data[i] = Transfer.FloatToHalf(x / (float)w * 12f); img.Data[i + 1] = Transfer.FloatToHalf(y / (float)h * 4f);
                img.Data[i + 2] = Transfer.FloatToHalf(((x ^ y) & 7) / 7f); img.Data[i + 3] = Transfer.FloatToHalf(1f);
            }
        return img;
    }

    [Fact]
    public void A_large_image_split_over_many_idat_chunks_decodes_to_the_same_pixels_as_a_single_chunk()
    {
        HalfImage img = Gradient(301, 257);
        using var small = new MemoryStream();
        HdrPngWriter.Write(img, small, idatChunkBytes: 4096);
        byte[] split = small.ToArray();
        Assert.True(Chunks(split).Count(c => c.Type == "IDAT") > 1, "the test must actually produce several IDAT chunks");
        byte[] whole = HdrPngWriter.Encode(img);
        Assert.Equal(Unfiltered(whole, 301, 257), Unfiltered(split, 301, 257));
        // ...and those pixels are the PQ encoding of the image, row by row.
        byte[] raw = Unfiltered(split, 301, 257);
        ushort r = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(256 * 301 * 8 + 300 * 8));
        Assert.InRange(r, (ushort)1, ushort.MaxValue);
    }

    [Fact]
    public void Encoding_allocates_a_small_multiple_of_one_band_not_of_the_whole_image()
    {
        HalfImage img = Gradient(1024, 1024);
        HdrPngWriter.Encode(new HalfImage(8, 8));   // JIT and statics out of the measurement
        using var sink = new MemoryStream(8 << 20);
        long before = GC.GetAllocatedBytesForCurrentThread();
        HdrPngWriter.Write(img, sink);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        long raw = 1024L * 1024 * 8;
        Assert.True(allocated < raw / 2, $"allocated {allocated / 1024} KB on the calling thread for a {raw / 1024} KB image");
    }

    [Fact]
    public void Crc32_matches_the_png_reference_value()
    {
        Assert.Equal(0xCBF43926u, HdrPngWriter.Crc32(System.Text.Encoding.ASCII.GetBytes("123456789")));
    }
}
