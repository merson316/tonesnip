using ToneSnip.Core.Config;
using ToneSnip.Core.Diagnostics;
using ToneSnip.Core.Imaging;
using Xunit;

namespace ToneSnip.Core.Tests;

public class HardeningTests
{
    [Fact]
    public void An_image_whose_size_overflows_is_refused_rather_than_built_on_a_wrapped_buffer()
    {
        // 65536 x 16385 x 4 wraps to 262,144 in 32-bit arithmetic.
        Assert.ThrowsAny<ArgumentException>(() => new BgraImage(65536, 16385, new byte[262_144]));
        Assert.ThrowsAny<Exception>(() => BgraImage.Blank(65536, 16385));
    }

    [Fact]
    public void Two_logs_on_one_file_from_two_threads_lose_no_lines()
    {
        // Several FileLog instances can share a path; they must share one lock or appends are lost.
        string path = Path.Combine(Path.GetTempPath(), "tonesnip-log-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            var a = new FileLog(path);
            var b = new FileLog(path);
            const int n = 400;
            Parallel.Invoke(
                () => { for (int i = 0; i < n; i++) a.Warn("a " + i); },
                () => { for (int i = 0; i < n; i++) b.Warn("b " + i); });
            Assert.Equal(2 * n, File.ReadAllLines(path).Length);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_full_log_is_rolled_to_one_previous_file_rather_than_wiped()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tonesnip-roll-" + Guid.NewGuid().ToString("N"));
        string? header = FileLog.Header;
        try
        {
            FileLog.Header = "ToneSnip 9.9.9, pid 42";
            var log = new FileLog(Path.Combine(dir, "tonesnip.log"), maxBytes: 200);
            Assert.Equal(Path.Combine(dir, "tonesnip.1.log"), log.PreviousPath);
            for (int i = 0; i < 20; i++) log.Warn("line " + i);

            // The evidence before the roll survives in the previous file, and every file starts with the header.
            string[] current = File.ReadAllLines(log.Path), previous = File.ReadAllLines(log.PreviousPath);
            Assert.EndsWith("ToneSnip 9.9.9, pid 42", current[0]);
            Assert.EndsWith("ToneSnip 9.9.9, pid 42", previous[0]);
            Assert.EndsWith("line 19", current[^1]);
            Assert.Contains(previous, l => l.EndsWith("line " + (19 - (current.Length - 1))));
            // Only one previous file is kept.
            Assert.Equal(2, Directory.GetFiles(dir).Length);
        }
        finally
        {
            FileLog.Header = header;
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Concurrent_saves_of_one_settings_file_both_succeed_and_leave_valid_json()
    {
        // Each save needs its own temp file name, or concurrent saves collide.
        string path = Path.Combine(Path.GetTempPath(), "tonesnip-json-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Parallel.For(0, 64, i => JsonFile.Save(path, new SnipSettings { JpegQuality = 50 + i % 50 }));
            (SnipSettings? back, string? err) = JsonFile.Load<SnipSettings>(path);
            Assert.Null(err);
            Assert.NotNull(back);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*.tmp"));
        }
        finally { File.Delete(path); }
    }
}
