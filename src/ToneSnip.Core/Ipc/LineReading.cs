using System.Text;

namespace ToneSnip.Core.Ipc;

/// <summary>Reads one newline-terminated UTF-8 line from a stream written by a process this one does not control.</summary>
public static class LineReading
{
    /// <summary>
    /// The line before the first newline (or before the end of the stream), or null when it runs past
    /// <paramref name="maxBytes"/>. Unlike <c>StreamReader.ReadLineAsync</c>, memory is bounded by the cap, and
    /// <paramref name="ct"/> bounds the wait so a silent client cannot block the one-client-at-a-time host pipe.
    /// </summary>
    public static async Task<string?> ReadLineAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        var buffer = new byte[Math.Min(maxBytes, 1024)];
        using var line = new MemoryStream();
        while (true)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (n == 0) break;
            int newline = Array.IndexOf(buffer, (byte)'\n', 0, n);
            int take = newline >= 0 ? newline : n;
            if (line.Length + take > maxBytes) return null;
            line.Write(buffer, 0, take);
            if (newline >= 0) break;
        }
        return Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r');
    }
}
