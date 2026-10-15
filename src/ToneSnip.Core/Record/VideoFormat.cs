namespace ToneSnip.Core.Record;

public enum VideoCodec { H264, Hevc }

/// <summary>Which codec a recording of a given size is encoded with, and at what bitrate.</summary>
public static class VideoFormat
{
    /// <summary>
    /// The largest frame the graphics cards' H.264 encoders take (AMD, Intel and NVIDIA alike stop at 4096 wide).
    /// Windows' own software encoder goes further, to H.264's level 5.2, but on the processor.
    /// </summary>
    public const int H264MaxWidth = 4096, H264MaxHeight = 2304;

    /// <summary>
    /// The codecs to try, in order. H.264 plays everywhere, so it is first wherever a hardware encoder can take the
    /// frame. A larger area (a whole ultrawide monitor) tries HEVC first, which the graphics card encodes at full size,
    /// and falls back to H.264 on the processor when there is no HEVC encoder; neither is ever scaled down.
    /// </summary>
    public static VideoCodec[] Candidates(int width, int height)
        => width <= H264MaxWidth && height <= H264MaxHeight ? [VideoCodec.H264, VideoCodec.Hevc] : [VideoCodec.Hevc, VideoCodec.H264];

    /// <summary>Bits per pixel per frame: screen content (text, flat colour, little motion) needs far less than camera
    /// footage. 1080p at 30 frames is about 6 Mbit/s in H.264; HEVC needs about two thirds of that.</summary>
    private const double H264BitsPerPixel = 0.1, HevcBitsPerPixel = 0.07;

    public const int MinBitrate = 1_000_000, MaxBitrate = 80_000_000;

    /// <summary>The mean bitrate the encoder aims at, in bits a second. A variable bitrate, so a quiet stretch costs
    /// little, and a still screen nothing at all, since no frames are sent for it.</summary>
    public static int Bitrate(VideoCodec codec, int width, int height, int fps)
    {
        double bpp = codec == VideoCodec.Hevc ? HevcBitsPerPixel : H264BitsPerPixel;
        double bits = (double)width * height * fps * bpp;
        return (int)Math.Clamp(Math.Round(bits), MinBitrate, MaxBitrate);
    }
}
