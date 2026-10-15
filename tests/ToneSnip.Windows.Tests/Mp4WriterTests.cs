using SharpGen.Runtime;
using ToneSnip.Core.Record;
using ToneSnip.Windows.Record;
using Vortice.MediaFoundation;
using Xunit;

namespace ToneSnip.Windows.Tests;

/// <summary>
/// <see cref="Mp4Writer"/> through Media Foundation's own encoders, read back with its source reader: the file plays,
/// at its size, tagged as the frames were made, with every frame at the time it was written. Skipped where Windows has
/// no Media Foundation (an N edition, or a Server without the feature).
/// </summary>
public sealed unsafe class Mp4WriterTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "tonesnip-test-" + Guid.NewGuid().ToString("N") + ".mp4");

    public void Dispose()
    {
        try { File.Delete(_path); } catch (IOException) { }
    }

    private static Mp4Writer? OpenOrSkip(string path, int width, int height, int fps)
    {
        try { return Mp4Writer.Open(path, width, height, fps, new TestLog()); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or SharpGenException or NotSupportedException)
        {
            Assert.Skip("Media Foundation cannot encode here: " + e.Message);
            return null;
        }
    }

    [Fact]
    public void A_recording_plays_back_at_its_size_and_times()
    {
        const int W = 192, H = 108;
        TimeSpan[] stamps = Enumerable.Range(0, 20).Select(i => TimeSpan.FromTicks(i * 333_333L + (i >= 10 ? 20_000_000L : 0))).ToArray();
        using (Mp4Writer writer = OpenOrSkip(_path, W, H, 30)!)
        {
            Assert.Equal(VideoCodec.H264, writer.Codec);
            for (int i = 0; i < stamps.Length; i++)
            {
                byte shade = (byte)(16 + i * 10);
                using IMFMediaBuffer frame = writer.Frame(p =>
                {
                    var bytes = new Span<byte>((void*)p, writer.FrameBytes);
                    bytes[..(W * H)].Fill(shade);
                    bytes[(W * H)..].Fill(128);
                });
                writer.Write(frame, stamps[i]);
            }
            writer.Finish();
        }

        MediaFactory.MFStartup(true).CheckError();
        try
        {
            using IMFSourceReader reader = MediaFactory.MFCreateSourceReaderFromURL(_path, null!);
            using (IMFMediaType native = reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, 0))
            {
                Assert.Equal(VideoFormatGuids.FromFourCC(new Vortice.FourCC("H264")), native.GetGUID(MediaTypeAttributeKeys.Subtype));
                Assert.Equal(((ulong)W << 32) | H, native.GetUInt64(MediaTypeAttributeKeys.FrameSize));
            }
            var read = new List<long>();
            while (true)
            {
                using IMFSample? sample = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out SourceReaderFlag flags, out long time);
                if ((flags & SourceReaderFlag.EndOfStream) != 0) break;
                if (sample != null) read.Add(time);
            }
            Assert.Equal(stamps.Length, read.Count);
            for (int i = 0; i < stamps.Length; i++)
                Assert.True(Math.Abs(read[i] - stamps[i].Ticks) < 10_000, $"frame {i} plays at {read[i] / 10_000.0:F1} ms, written at {stamps[i].TotalMilliseconds:F1} ms");
        }
        finally { MediaFactory.MFShutdown(); }
    }

    [Fact]
    public void An_area_no_encoder_takes_is_refused_and_leaves_no_file()
    {
        try
        {
            using Mp4Writer? writer = Mp4Writer.Open(_path, 16384, 16384, 30, new TestLog());
            Assert.Fail("a 16384 x 16384 frame was accepted with " + writer.Codec);
        }
        catch (DllNotFoundException e) { Assert.Skip("Media Foundation is not installed: " + e.Message); }
        catch (NotSupportedException) { }
        Assert.False(File.Exists(_path));
    }
}
