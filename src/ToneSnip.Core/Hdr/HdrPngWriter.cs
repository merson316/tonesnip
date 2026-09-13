using System.Buffers.Binary;
using System.IO.Compression;
using ToneSnip.Core.Color;
using ToneSnip.Core.Imaging;

namespace ToneSnip.Core.Hdr;

/// <summary>
/// 16-bit RGBA PNG with a cICP chunk (BT.2020 primaries, PQ transfer, full range), which browsers and recent viewers
/// read as HDR.
/// <para>
/// Streamed to keep memory bounded: rows are converted a band at a time (in parallel within the band), filtered with
/// Up (screenshots are mostly flat, so most bytes become zero) and deflated straight into IDAT chunks of
/// <see cref="DefaultIdatChunkBytes"/>. PNG allows any number of IDAT chunks; decoders concatenate them.
/// </para>
/// </summary>
public static class HdrPngWriter
{
    public const float NitsPerUnit = 80f;
    /// <summary>The compressed bytes held before an IDAT chunk is written out.</summary>
    public const int DefaultIdatChunkBytes = 1 << 20;
    /// <summary>Rows converted together: the unit of the parallel work, and of the memory held for it.</summary>
    private const int BandRows = 64;
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static byte[] Encode(HalfImage img) { using var ms = new MemoryStream(); Write(img, ms); return ms.ToArray(); }

    public static void Write(HalfImage img, Stream output, int idatChunkBytes = DefaultIdatChunkBytes)
    {
        output.Write(Signature);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, img.Width); BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), img.Height);
        ihdr[8] = 16; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;   // depth, colour type RGBA, compression, filter, interlace
        Chunk(output, "IHDR", ihdr);
        Chunk(output, "cICP", new byte[] { 9, 16, 0, 1 });
        using (var idat = new IdatStream(output, Math.Max(256, idatChunkBytes)))
        using (var z = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
            WriteRows(img, z);
        Chunk(output, "IEND", Array.Empty<byte>());
    }

    private static void WriteRows(HalfImage img, Stream z)
    {
        int stride = checked(img.Width * 8);
        int band = Math.Min(BandRows, img.Height);
        var raw = new byte[checked(stride * band)];
        var previous = new byte[stride];
        var line = new byte[stride + 1];
        ushort[] d = img.Data;
        bool parallel = (long)img.Width * img.Height >= 1 << 16;   // thread fan-out costs more than a tiny image
        for (int top = 0; top < img.Height; top += band)
        {
            int rows = Math.Min(band, img.Height - top);
            void Row(int k)
            {
                int y = top + k, rowStart = k * stride;
                for (int x = 0; x < img.Width; x++)
                {
                    int i = (y * img.Width + x) * 4, o = rowStart + x * 8;
                    float r = Transfer.HalfToFloat(d[i]), g = Transfer.HalfToFloat(d[i + 1]), b = Transfer.HalfToFloat(d[i + 2]), a = Transfer.HalfToFloat(d[i + 3]);
                    ColorMath.Bt709To2020(ref r, ref g, ref b);
                    BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(o), Pq16(r)); BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(o + 2), Pq16(g));
                    BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(o + 4), Pq16(b));
                    // Round-half-up (Math.Round defaults to round-to-even). NaN alpha is 0.
                    BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(o + 6), (ushort)(Math.Clamp(float.IsNaN(a) ? 0f : a, 0f, 1f) * 65535f + 0.5f));
                }
            }
            if (parallel) Parallel.For(0, rows, Row); else for (int k = 0; k < rows; k++) Row(k);
            for (int k = 0; k < rows; k++)
            {
                ReadOnlySpan<byte> current = raw.AsSpan(k * stride, stride);
                if (top + k == 0) { line[0] = 0; current.CopyTo(line.AsSpan(1)); }   // None: there is no row above
                else
                {
                    line[0] = 2;   // Up
                    for (int i = 0; i < stride; i++) line[i + 1] = (byte)(current[i] - previous[i]);
                }
                current.CopyTo(previous);
                z.Write(line);
            }
        }
    }

    /// <summary>Takes the deflate output and writes it out as IDAT chunks of a bounded size.</summary>
    private sealed class IdatStream(Stream output, int chunkBytes) : Stream
    {
        private readonly byte[] _buffer = new byte[chunkBytes];
        private int _used;

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> data)
        {
            while (data.Length > 0)
            {
                int n = Math.Min(data.Length, _buffer.Length - _used);
                data[..n].CopyTo(_buffer.AsSpan(_used));
                _used += n; data = data[n..];
                if (_used == _buffer.Length) Emit();
            }
        }

        private void Emit()
        {
            if (_used == 0) return;
            Chunk(output, "IDAT", _buffer.AsSpan(0, _used));
            _used = 0;
        }

        protected override void Dispose(bool disposing) { if (disposing) Emit(); base.Dispose(disposing); }
        public override void Flush() { }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static ushort Pq16(float linear)
    {
        float v = Math.Clamp(Transfer.PqEncode(Math.Max(linear, 0f) * NitsPerUnit), 0f, 1f);
        return (ushort)(v * 65535f + 0.5f);   // round-half-up, not banker's rounding
    }

    private static void Chunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> head = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(head, data.Length);
        System.Text.Encoding.ASCII.GetBytes(type, head[4..]);
        s.Write(head);
        s.Write(data);
        uint crc = Crc32Update(Crc32Update(0xFFFFFFFFu, head[4..]), data) ^ 0xFFFFFFFFu;   // over the type and the data
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(tail, crc);
        s.Write(tail);
    }

    private static readonly uint[] CrcTable = BuildCrc();
    private static uint[] BuildCrc() { var t = new uint[256]; for (uint n = 0; n < 256; n++) { uint c = n; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[n] = c; } return t; }
    public static uint Crc32(ReadOnlySpan<byte> bytes) => Crc32Update(0xFFFFFFFFu, bytes) ^ 0xFFFFFFFFu;
    private static uint Crc32Update(uint c, ReadOnlySpan<byte> bytes) { foreach (byte b in bytes) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8); return c; }
}
