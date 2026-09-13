using System.Buffers.Binary;
using System.Text;
using ToneSnip.Core.Hdr;
using Xunit;

namespace ToneSnip.Core.Tests.Hdr;

public class UltraHdrContainerTests
{
    // A minimal "JPEG": SOI, one APP0 segment, a fake scan, EOI.
    private static byte[] FakeJpeg(byte fill, int payload)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 }); ms.Write(new byte[14]);
        ms.Write(new byte[] { 0xFF, 0xDA, 0x00, 0x02 }); for (int i = 0; i < payload; i++) ms.WriteByte(fill);
        ms.Write(new byte[] { 0xFF, 0xD9 });
        return ms.ToArray();
    }

    private static List<(byte Marker, int Offset, byte[] Data)> Segments(byte[] jpeg)
    {
        var list = new List<(byte, int, byte[])>();
        int p = 2;
        while (p + 4 <= jpeg.Length && jpeg[p] == 0xFF)
        {
            byte marker = jpeg[p + 1]; int len = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(p + 2));
            list.Add((marker, p, jpeg[(p + 4)..(p + 2 + len)]));
            if (marker == 0xDA) break;
            p += 2 + len;
        }
        return list;
    }

    [Fact]
    public void Base_gets_xmp_and_mpf_and_the_gain_map_follows_at_the_declared_offset()
    {
        byte[] baseJpeg = FakeJpeg(0x11, 100), gain = FakeJpeg(0x22, 40);
        var meta = new UltraHdrMeta(-0.1f, 2.5f);
        byte[] outp = UltraHdrContainer.Assemble(baseJpeg, gain, meta);
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, outp[..2]);
        var segs = Segments(outp);
        Assert.Equal(0xE1, segs[0].Marker);   // XMP first
        string xmp = Encoding.UTF8.GetString(segs[0].Data);
        Assert.StartsWith("http://ns.adobe.com/xap/1.0/\0", xmp);
        Assert.Contains("hdrgm:Version=\"1.0\"", xmp);
        Assert.Contains("Item:Semantic=\"GainMap\"", xmp);
        Assert.Equal(0xE2, segs[1].Marker);   // MPF second
        byte[] mpf = segs[1].Data;
        Assert.Equal("MPF\0", Encoding.ASCII.GetString(mpf, 0, 4));
        int tiff = 4;   // TIFF header start, offsets are relative to it
        Assert.Equal((byte)'I', mpf[tiff]); Assert.Equal((byte)'I', mpf[tiff + 1]);   // little-endian byte order mark
        Assert.Equal(42, BinaryPrimitives.ReadUInt16LittleEndian(mpf.AsSpan(tiff + 2)));   // TIFF magic
        int firstIfdOffset = BinaryPrimitives.ReadInt32LittleEndian(mpf.AsSpan(tiff + 4));
        Assert.Equal(8, firstIfdOffset);
        int ifd = tiff + firstIfdOffset;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(mpf.AsSpan(ifd));
        Assert.Equal(3, count);

        // Entry 1: MPFVersion (0xB000), UNDEFINED × 4, value "0100"
        int mpfVersionEntry = ifd + 2;
        Assert.Equal(0xB000, BinaryPrimitives.ReadUInt16LittleEndian(mpf.AsSpan(mpfVersionEntry)));
        Assert.Equal(7, BinaryPrimitives.ReadUInt16LittleEndian(mpf.AsSpan(mpfVersionEntry + 2)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(mpf.AsSpan(mpfVersionEntry + 4)));
        Assert.Equal("0100", Encoding.ASCII.GetString(mpf, mpfVersionEntry + 8, 4));

        // Entry 2: NumberOfImages (0xB001), LONG, value 2
        int numberOfImagesEntry = mpfVersionEntry + 12;
        Assert.Equal(0xB001, BinaryPrimitives.ReadUInt16LittleEndian(mpf.AsSpan(numberOfImagesEntry)));
        Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(mpf.AsSpan(numberOfImagesEntry + 2)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(mpf.AsSpan(numberOfImagesEntry + 4)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(mpf.AsSpan(numberOfImagesEntry + 8)));

        // Entry 3: MPEntry (0xB002), UNDEFINED × 32; its value is the TIFF-relative offset of the two MP entries
        int entryTag = numberOfImagesEntry + 12;
        Assert.Equal(0xB002, BinaryPrimitives.ReadUInt16LittleEndian(mpf.AsSpan(entryTag)));
        Assert.Equal(7, BinaryPrimitives.ReadUInt16LittleEndian(mpf.AsSpan(entryTag + 2)));
        Assert.Equal(32u, BinaryPrimitives.ReadUInt32LittleEndian(mpf.AsSpan(entryTag + 4)));
        int entries = tiff + BinaryPrimitives.ReadInt32LittleEndian(mpf.AsSpan(entryTag + 8));

        // No next IFD.
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(mpf.AsSpan(entryTag + 12)));

        // MP entry 1: primary image, baseline MP primary + JPEG (attribute 0x030000), offset 0, no dependent images.
        Assert.Equal(0x030000u, BinaryPrimitives.ReadUInt32LittleEndian(mpf.AsSpan(entries)));
        uint primarySize = BinaryPrimitives.ReadUInt32LittleEndian(mpf.AsSpan(entries + 4));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(mpf.AsSpan(entries + 8)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(mpf.AsSpan(entries + 12)));   // dependent image 1 number
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(mpf.AsSpan(entries + 14)));   // dependent image 2 number

        // MP entry 2: gain map, undefined attribute (0x000000), no dependent images.
        int gainEntry = entries + 16;
        Assert.Equal(0x000000u, BinaryPrimitives.ReadUInt32LittleEndian(mpf.AsSpan(gainEntry)));
        uint gainSize = BinaryPrimitives.ReadUInt32LittleEndian(mpf.AsSpan(gainEntry + 4));
        uint gainOffset = BinaryPrimitives.ReadUInt32LittleEndian(mpf.AsSpan(gainEntry + 8));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(mpf.AsSpan(gainEntry + 12)));   // dependent image 1 number
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(mpf.AsSpan(gainEntry + 14)));   // dependent image 2 number
        // The embedded gain map is the input plus its own XMP segment; it starts right after the primary image, and
        // MPF offsets are measured from the TIFF header ("II") inside the APP2 segment.
        int embeddedLen = outp.Length - (int)primarySize;
        Assert.True(embeddedLen > gain.Length);
        Assert.Equal((uint)embeddedLen, gainSize);
        Assert.Contains($"Item:Length=\"{embeddedLen}\"", xmp);
        int tiffHeaderInFile = segs[1].Offset + 2 + 2 + 4;   // marker + length field + "MPF\0"
        Assert.Equal((uint)(primarySize - tiffHeaderInFile), gainOffset);
        byte[] embeddedGain = outp[(int)primarySize..];
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, embeddedGain[..2]);
        Assert.Equal(gain[^(gain.Length - 2)..], embeddedGain[^(gain.Length - 2)..]);   // original payload after the inserted XMP
        var embedded = Segments(embeddedGain);
        Assert.Contains(embedded, s => s.Marker == 0xE1 && Encoding.UTF8.GetString(s.Data).Contains("hdrgm:GainMapMax=\"2.5"));
    }
}
