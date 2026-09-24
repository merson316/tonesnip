namespace ToneSnip.Core.Hdr;

/// <summary>
/// The height of a staging band a frame on the graphics card is read back through, a band at a time, so the readback
/// never holds a second full frame in system memory.
/// </summary>
public static class ReadbackBand
{
    /// <summary>The tallest texture D3D11 can make (D3D11_REQ_TEXTURE2D_U_OR_V_DIMENSION). A staging band is a
    /// texture, so a band this tall or taller fails to be created at all.</summary>
    public const int MaxTextureDimension = 16384;

    /// <summary>
    /// Rows of a band <paramref name="width"/> pixels wide that fit in <paramref name="bandBytes"/>, for a read of
    /// <paramref name="height"/> rows: at least one, no more than the read needs, and never past
    /// <see cref="MaxTextureDimension"/>. Without the cap, a narrow read (a few pixels wide, as a thin lasso or selection
    /// is) asked for a band hundreds of thousands of rows tall, which the driver refuses.
    /// </summary>
    public static int Rows(int width, int height, int bytesPerPixel, int bandBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        long fit = bandBytes / ((long)width * bytesPerPixel);
        return (int)Math.Clamp(fit, 1, Math.Min(height, MaxTextureDimension));
    }
}
