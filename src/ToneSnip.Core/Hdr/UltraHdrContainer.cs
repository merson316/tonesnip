using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace ToneSnip.Core.Hdr;

public sealed record UltraHdrMeta(float GainMapMin, float GainMapMax)
{
    public const float Gamma = 1f, OffsetSdr = GainMap.Offset, OffsetHdr = GainMap.Offset, HdrCapacityMin = 0f;
    public float HdrCapacityMax => Math.Max(GainMapMax, 0.001f);
}

/// <summary>
/// UltraHDR v1 container: the SDR JPEG carries an XMP directory and an MPF index; the gain-map JPEG (with its own XMP
/// parameters) is appended after it. SDR viewers show the base; HDR-aware ones rebuild the highlights.
/// </summary>
public static class UltraHdrContainer
{
    private const string XmpNs = "http://ns.adobe.com/xap/1.0/\0";

    public static byte[] Assemble(byte[] baseJpeg, byte[] gainJpeg, UltraHdrMeta meta)
    {
        Require(baseJpeg); Require(gainJpeg);
        byte[] gainWithXmp = InsertAfterSoi(gainJpeg, App1(GainMapXmp(meta)));
        byte[] xmp = App1(BaseXmp(meta, gainWithXmp.Length));
        // MPF offsets are measured from the start of the TIFF header (after "MPF\0"); the primary's size is the whole base
        // file including the segments we insert, and the gain map starts right after it.
        int mpfLen = 2 + 2 + MpfBodyLength;                                   // marker + length field + body
        int primarySize = baseJpeg.Length + xmp.Length + mpfLen;
        int tiffHeaderPos = 2 + xmp.Length + 2 + 2 + 4;                        // SOI + APP1 + APP2 marker/length + "MPF\0"
        byte[] mpf = App2Mpf(primarySize, gainWithXmp.Length, gainOffset: primarySize - tiffHeaderPos);
        var ms = new MemoryStream(primarySize + gainWithXmp.Length);
        ms.Write(baseJpeg, 0, 2); ms.Write(xmp); ms.Write(mpf); ms.Write(baseJpeg, 2, baseJpeg.Length - 2);
        ms.Write(gainWithXmp);
        return ms.ToArray();
    }

    public static string BaseXmp(UltraHdrMeta meta, int gainMapLength) =>
        "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
        "<rdf:Description rdf:about=\"\" xmlns:Container=\"http://ns.google.com/photos/1.0/container/\" xmlns:Item=\"http://ns.google.com/photos/1.0/container/item/\" xmlns:hdrgm=\"http://ns.adobe.com/hdr-gain-map/1.0/\" hdrgm:Version=\"1.0\">" +
        "<Container:Directory><rdf:Seq>" +
        "<rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Semantic=\"Primary\" Item:Mime=\"image/jpeg\"/></rdf:li>" +
        $"<rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Semantic=\"GainMap\" Item:Mime=\"image/jpeg\" Item:Length=\"{gainMapLength}\"/></rdf:li>" +
        "</rdf:Seq></Container:Directory></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

    public static string GainMapXmp(UltraHdrMeta m) =>
        "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
        "<rdf:Description rdf:about=\"\" xmlns:hdrgm=\"http://ns.adobe.com/hdr-gain-map/1.0/\" hdrgm:Version=\"1.0\"" +
        $" hdrgm:GainMapMin=\"{F(m.GainMapMin)}\" hdrgm:GainMapMax=\"{F(m.GainMapMax)}\" hdrgm:Gamma=\"{F(UltraHdrMeta.Gamma)}\"" +
        $" hdrgm:OffsetSDR=\"{F(UltraHdrMeta.OffsetSdr)}\" hdrgm:OffsetHDR=\"{F(UltraHdrMeta.OffsetHdr)}\"" +
        $" hdrgm:HDRCapacityMin=\"{F(UltraHdrMeta.HdrCapacityMin)}\" hdrgm:HDRCapacityMax=\"{F(m.HdrCapacityMax)}\" hdrgm:BaseRenditionIsHDR=\"False\"/>" +
        "</rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

    private static string F(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    private static byte[] App1(string xmp)
    {
        byte[] body = Encoding.UTF8.GetBytes(XmpNs + xmp);
        if (body.Length + 2 > 0xFFFF) throw new ArgumentException("XMP packet too large for one APP1 segment");
        var seg = new byte[4 + body.Length];
        seg[0] = 0xFF; seg[1] = 0xE1; BinaryPrimitives.WriteUInt16BigEndian(seg.AsSpan(2), (ushort)(body.Length + 2));
        body.CopyTo(seg, 4);
        return seg;
    }

    // "MPF\0" + TIFF header (8) + IFD: count(2) + 3 entries (36) + next(4) + 2 MP entries (32) = 86 bytes
    private const int MpfBodyLength = 4 + 8 + 2 + 36 + 4 + 32;

    private static byte[] App2Mpf(int primarySize, int gainSize, int gainOffset)
    {
        var b = new byte[4 + MpfBodyLength];
        b[0] = 0xFF; b[1] = 0xE2; BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), (ushort)(MpfBodyLength + 2));
        int p = 4;
        Encoding.ASCII.GetBytes("MPF\0").CopyTo(b, p); p += 4;
        int tiff = p;
        b[p] = (byte)'I'; b[p + 1] = (byte)'I'; BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p + 2), 42); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p + 4), 8); p += 8;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p), 3); p += 2;
        // MPFVersion: UNDEFINED ×4 "0100"
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p), 0xB000); BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p + 2), 7); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p + 4), 4); Encoding.ASCII.GetBytes("0100").CopyTo(b, p + 8); p += 12;
        // NumberOfImages: LONG 2
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p), 0xB001); BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p + 2), 4); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p + 4), 1); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p + 8), 2); p += 12;
        // MPEntry: UNDEFINED ×32 at offset (after next-IFD pointer)
        int entriesOffset = (p + 12 + 4) - tiff;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p), 0xB002); BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p + 2), 7); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p + 4), 32); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p + 8), (uint)entriesOffset); p += 12;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p), 0); p += 4;   // next IFD: none
        // Entry 1: primary image (attribute 0x030000 = baseline MP primary, JPEG), offset 0
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p), 0x030000); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p + 4), (uint)primarySize); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p + 8), 0); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p + 12), 0); p += 16;
        // Entry 2: gain map (undefined type, JPEG)
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p), 0x000000); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p + 4), (uint)gainSize); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p + 8), (uint)gainOffset); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p + 12), 0); p += 16;
        return b;
    }

    private static byte[] InsertAfterSoi(byte[] jpeg, byte[] segment)
    {
        var o = new byte[jpeg.Length + segment.Length];
        o[0] = jpeg[0]; o[1] = jpeg[1]; segment.CopyTo(o, 2); Buffer.BlockCopy(jpeg, 2, o, 2 + segment.Length, jpeg.Length - 2);
        return o;
    }

    private static void Require(byte[] jpeg) { if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) throw new ArgumentException("not a JPEG (no SOI)"); }
}
