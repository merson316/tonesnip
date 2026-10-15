using ToneSnip.Core.Record;
using Xunit;

namespace ToneSnip.Core.Tests.Record;

public class VideoFormatTests
{
    [Theory]
    [InlineData(1920, 1080, VideoCodec.H264)]
    [InlineData(4096, 2304, VideoCodec.H264)]
    [InlineData(5120, 1440, VideoCodec.Hevc)]
    [InlineData(3840, 2400, VideoCodec.Hevc)]
    public void H264_comes_first_wherever_a_graphics_card_can_encode_it(int width, int height, VideoCodec first)
    {
        VideoCodec[] order = VideoFormat.Candidates(width, height);
        Assert.Equal(first, order[0]);
        Assert.Equal(2, order.Distinct().Count());
    }

    [Fact]
    public void The_bitrate_follows_the_pixel_rate_within_its_bounds()
    {
        Assert.Equal(6_220_800, VideoFormat.Bitrate(VideoCodec.H264, 1920, 1080, 30));
        Assert.True(VideoFormat.Bitrate(VideoCodec.Hevc, 1920, 1080, 30) < VideoFormat.Bitrate(VideoCodec.H264, 1920, 1080, 30));
        Assert.Equal(VideoFormat.MinBitrate, VideoFormat.Bitrate(VideoCodec.H264, 64, 64, 30));
        Assert.Equal(VideoFormat.MaxBitrate, VideoFormat.Bitrate(VideoCodec.H264, 7680, 4320, 60));
    }
}
