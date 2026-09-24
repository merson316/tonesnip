namespace ToneSnip.Core.Imaging;

/// <summary>Reads a PNG's pixel size from its header, so a pin can be sized without decoding the whole image.</summary>
public static class PngInfo
{
    private static ReadOnlySpan<byte> Signature => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>The width and height from the IHDR chunk, which the format requires to come first. False for anything
    /// that is not a PNG with a positive size.</summary>
    public static bool TrySize(ReadOnlySpan<byte> png, out int width, out int height)
    {
        width = height = 0;
        // Signature (8), then the IHDR chunk: length (4), type (4), width (4), height (4), all big-endian.
        if (png.Length < 24 || !png[..8].SequenceEqual(Signature) || !png.Slice(12, 4).SequenceEqual("IHDR"u8)) return false;
        int w = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.Slice(16, 4));
        int h = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.Slice(20, 4));
        if (w <= 0 || h <= 0) return false;
        (width, height) = (w, h);
        return true;
    }
}
