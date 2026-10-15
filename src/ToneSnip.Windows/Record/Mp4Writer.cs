using SharpGen.Runtime;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Core.Record;
using Vortice.MediaFoundation;
using VideoFormat = ToneSnip.Core.Record.VideoFormat;

namespace ToneSnip.Windows.Record;

/// <summary>
/// A recording's file: NV12 frames from system memory, encoded to H.264 or HEVC in MP4 by Media Foundation's sink
/// writer with the encoders Windows has, the graphics card's when it has one. The stream is tagged BT.709 at limited
/// range, as <see cref="Nv12"/> writes it, and encoded at a variable bitrate around <see cref="VideoFormat.Bitrate"/>.
/// Frames carry their own timestamps, so the file is variable frame rate: MP4 keeps each frame on screen until the
/// next one's time.
/// <para>Not thread-safe: the recorder's thread owns it.</para>
/// </summary>
internal sealed unsafe class Mp4Writer : IDisposable
{
    private static readonly Guid Nv12Format = VideoFormatGuids.FromFourCC(new Vortice.FourCC("NV12"));
    private static readonly Guid H264Format = VideoFormatGuids.FromFourCC(new Vortice.FourCC("H264"));
    private static readonly Guid HevcFormat = VideoFormatGuids.FromFourCC(new Vortice.FourCC("HEVC"));

    // ICodecAPI properties (codecapi.h), passed to the encoder through SetInputMediaType's encoding parameters.
    private static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    private static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    private static readonly Guid BFrames = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    private const uint UnconstrainedVbr = 2;

    // MF_MT_* values: MFVideoInterlace_Progressive, MFVideoTransferMatrix_BT709, MFNominalRange_16_235,
    // MFVideoPrimaries_BT709, MFVideoTransFunc_709, eAVEncH264VProfile_High, eAVEncH265VProfile_Main_420_8.
    private const uint Progressive = 2, Bt709Matrix = 1, LimitedRange = 2, Bt709Primaries = 2, Bt709Transfer = 5;
    private const uint H264High = 100, HevcMain = 1;

    private readonly IMFSinkWriter _writer;
    private readonly int _stream;
    private readonly long _frameTicks;
    private bool _finished, _disposed;

    private Mp4Writer(IMFSinkWriter writer, int stream, VideoCodec codec, int width, int height, int fps)
    {
        _writer = writer;
        _stream = stream;
        _frameTicks = TimeSpan.TicksPerSecond / fps;
        Codec = codec;
        Width = width;
        Height = height;
    }

    public VideoCodec Codec { get; }
    public int Width { get; }
    public int Height { get; }
    public int FrameBytes => Nv12.Size(Width, Height);

    /// <summary>
    /// Opens <paramref name="path"/> for a <paramref name="width"/> x <paramref name="height"/> recording, trying the
    /// codecs <see cref="VideoFormat.Candidates"/> names until one takes the size. Throws when none does, or when Media
    /// Foundation itself is missing (an N edition of Windows without its Media Feature Pack).
    /// </summary>
    public static Mp4Writer Open(string path, int width, int height, int fps, ILog log)
    {
        MediaFactory.MFStartup(true).CheckError();
        var failures = new List<string>();
        foreach (VideoCodec codec in VideoFormat.Candidates(width, height))
        {
            try
            {
                Mp4Writer writer = Create(path, codec, width, height, fps);
                log.Info($"record: {codec} {width}x{height} at up to {fps} fps, {VideoFormat.Bitrate(codec, width, height, fps) / 1000} kbit/s");
                return writer;
            }
            catch (SharpGenException e)
            {
                failures.Add($"{codec}: {e.ResultCode.Code:X8}");
                log.Info($"record: {codec} cannot encode {width}x{height} here ({e.ResultCode.Code:X8}); trying the next codec");
                TryDelete(path);
            }
        }
        MediaFactory.MFShutdown();
        throw new NotSupportedException($"No video encoder on this PC takes a {width} x {height} area ({string.Join(", ", failures)})");
    }

    private static Mp4Writer Create(string path, VideoCodec codec, int width, int height, int fps)
    {
        using IMFAttributes attributes = MediaFactory.MFCreateAttributes(1);
        attributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
        IMFSinkWriter writer = MediaFactory.MFCreateSinkWriterFromURL(path, null!, attributes);
        try
        {
            int bitrate = VideoFormat.Bitrate(codec, width, height, fps);
            using IMFMediaType output = Video(codec == VideoCodec.Hevc ? HevcFormat : H264Format, width, height, fps);
            output.Set(MediaTypeAttributeKeys.AvgBitrate, (uint)bitrate);
            output.Set(MediaTypeAttributeKeys.Mpeg2Profile, codec == VideoCodec.Hevc ? HevcMain : H264High);
            int stream = writer.AddStream(output);
            using IMFMediaType input = Video(Nv12Format, width, height, fps);
            using IMFAttributes encoder = MediaFactory.MFCreateAttributes(4);
            encoder.Set(RateControlMode, UnconstrainedVbr);
            encoder.Set(MeanBitRate, (uint)bitrate);
            // A key frame at least every two seconds of frames, so a long recording can be scrubbed.
            encoder.Set(GopSize, (uint)(fps * 2));
            // No B-frames: they put every frame one frame late in the file, behind the decoder's reordering delay, and
            // a variable frame rate recording gains little from them.
            encoder.Set(BFrames, 0u);
            writer.SetInputMediaType(stream, input, encoder);
            writer.BeginWriting();
            return new Mp4Writer(writer, stream, codec, width, height, fps);
        }
        catch
        {
            writer.Dispose();
            throw;
        }
    }

    private static IMFMediaType Video(Guid subtype, int width, int height, int fps)
    {
        IMFMediaType t = MediaFactory.MFCreateMediaType();
        t.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        t.Set(MediaTypeAttributeKeys.Subtype, subtype);
        t.Set(MediaTypeAttributeKeys.InterlaceMode, Progressive);
        t.Set(MediaTypeAttributeKeys.FrameSize, Pack((uint)width, (uint)height));
        t.Set(MediaTypeAttributeKeys.FrameRate, Pack((uint)fps, 1));
        t.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
        t.Set(MediaTypeAttributeKeys.YuvMatrix, Bt709Matrix);
        t.Set(MediaTypeAttributeKeys.VideoNominalRange, LimitedRange);
        t.Set(MediaTypeAttributeKeys.VideoPrimaries, Bt709Primaries);
        t.Set(MediaTypeAttributeKeys.TransferFunction, Bt709Transfer);
        return t;
    }

    private static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;

    /// <summary>A buffer for one frame, which <paramref name="fill"/> writes <see cref="FrameBytes"/> bytes into. The
    /// caller owns it, and may hand it to <see cref="Write"/> more than once.</summary>
    public IMFMediaBuffer Frame(Action<IntPtr> fill)
    {
        IMFMediaBuffer buffer = MediaFactory.MFCreateMemoryBuffer(FrameBytes);
        try
        {
            buffer.Lock(out IntPtr data, out _, out _);
            try { fill(data); }
            finally { buffer.Unlock(); }
            buffer.CurrentLength = FrameBytes;
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }

    /// <summary>Writes a frame at <paramref name="at"/>, after the last one's. The writer takes its own reference to
    /// the buffer. Blocks while the encoder is behind.</summary>
    public void Write(IMFMediaBuffer frame, TimeSpan at)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using IMFSample sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(frame);
        sample.SampleTime = at.Ticks;
        // A nominal frame: the MP4 sink shows each frame until the next one's time, and the last for this long.
        sample.SampleDuration = _frameTicks;
        _writer.WriteSample(_stream, sample);
    }

    /// <summary>Drains the encoder and writes the file's index; without it the MP4 does not play.</summary>
    public void Finish()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished) return;
        _finished = true;
        _writer.Finalize();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writer.Dispose();
        MediaFactory.MFShutdown();
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
