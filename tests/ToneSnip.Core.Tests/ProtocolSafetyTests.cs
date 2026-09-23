using System.Text;
using ToneSnip.Core.Capture;
using ToneSnip.Core.Ipc;
using Xunit;

namespace ToneSnip.Core.Tests;

public class ProtocolSafetyTests
{
    [Fact]
    public void The_second_launch_pipe_differs_per_session_so_two_signed_in_users_never_share_one()
    {
        // Named pipes are machine-wide while the instance mutex is per session.
        Assert.NotEqual(Protocol.HostPipeFor(1), Protocol.HostPipeFor(2));
        Assert.StartsWith("tonesnip", Protocol.HostPipeFor(1));
    }

    [Fact]
    public void An_output_knows_its_bounds()
    {
        var o = new OutputInfo(1, @"\\.\DISPLAY2", 3440, -300, 1080, 1920, false, 80f, 1000f);
        Assert.Equal(new ToneSnip.Core.Geometry.IntRect(3440, -300, 1080, 1920), o.Bounds);
    }

    [Fact]
    public async Task A_line_is_read_up_to_its_newline()
    {
        using var s = new MemoryStream(Encoding.UTF8.GetBytes("--snip region\nignored"));
        Assert.Equal("--snip region", await LineReading.ReadLineAsync(s, 4096, CancellationToken.None));
    }

    [Fact]
    public async Task A_line_longer_than_the_cap_is_refused_rather_than_buffered()
    {
        using var s = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 5000) + "\n"));
        Assert.Null(await LineReading.ReadLineAsync(s, 4096, CancellationToken.None));
    }

    [Fact]
    public async Task A_client_that_never_sends_a_newline_is_given_up_on()
    {
        // The pipe serves one client at a time, so a silent client must not block later launches.
        var never = new NeverEndingStream();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LineReading.ReadLineAsync(never, 4096, cts.Token));
    }

    private sealed class NeverEndingStream : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => 0; public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { await Task.Delay(Timeout.Infinite, ct); return 0; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
