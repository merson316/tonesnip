using System.Runtime.InteropServices;
using ToneSnip.Core.Imaging;

namespace ToneSnip.App.Output;

/// <summary>Puts CF_DIBV5 (32-bit BGRA with alpha) and the registered "PNG" format on the clipboard in one transaction.</summary>
public static partial class ClipboardWriter
{
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool OpenClipboard(IntPtr owner);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseClipboard();
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool EmptyClipboard();
    [LibraryImport("user32.dll")] private static partial IntPtr SetClipboardData(uint format, IntPtr data);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial uint RegisterClipboardFormatW(string name);
    [LibraryImport("kernel32.dll")] private static partial IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [LibraryImport("kernel32.dll")] private static partial IntPtr GlobalLock(IntPtr h);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GlobalUnlock(IntPtr h);
    [LibraryImport("kernel32.dll")] private static partial IntPtr GlobalFree(IntPtr h);
    [LibraryImport("kernel32.dll")] private static partial UIntPtr GlobalSize(IntPtr h);
    private const uint GmemMoveable = 0x0002, CfDibV5 = 17;

    private static readonly object QueueGate = new();
    /// <summary>Held for one whole open-to-close transaction, so a write made directly (the snip's own copy, the
    /// editor's save) waits for one in flight rather than failing to open the clipboard it holds.</summary>
    private static readonly object WriteGate = new();
    /// <summary>The last write queued by <see cref="Enqueue"/>; the next one runs after it. Under
    /// <see cref="QueueGate"/>.</summary>
    private static Task _tail = Task.CompletedTask;

    /// <summary>
    /// Runs <paramref name="write"/> on the thread pool after every write queued before it has finished, so writes
    /// started in one order land on the clipboard in that order. Each write waits up to half a second for a clipboard
    /// another app holds, so two started close together on separate tasks could otherwise land the wrong way round,
    /// leaving the older text or picture on the clipboard. The returned task carries this write's own failure; one
    /// write failing does not stop the next.
    /// </summary>
    public static Task Enqueue(Action write)
    {
        lock (QueueGate)
        {
            Task next = _tail.ContinueWith(_ => write(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            _tail = next;
            return next;
        }
    }

    /// <summary><see cref="Set"/> through <see cref="Enqueue"/>.</summary>
    public static Task SetQueued(BgraImage image, byte[] png, Core.Diagnostics.ILog? log = null) => Enqueue(() => Set(image, png, log));

    /// <summary><see cref="SetText"/> through <see cref="Enqueue"/>.</summary>
    public static Task SetTextQueued(string text, Core.Diagnostics.ILog? log = null) => Enqueue(() => SetText(text, log));

    /// <summary>
    /// Writes both formats. Callable from any thread: the Win32 clipboard needs only its open/close pair on one thread.
    /// If the PNG format fails after the DIB succeeded, that is logged rather than thrown.
    /// </summary>
    public static void Set(BgraImage image, byte[] png, Core.Diagnostics.ILog? log = null)
    {
        lock (WriteGate) SetLocked(image, png, log);
    }

    private static void SetLocked(BgraImage image, byte[] png, Core.Diagnostics.ILog? log)
    {
        bool open = false;
        for (int i = 0; i < 10 && !(open = OpenClipboard(IntPtr.Zero)); i++) Thread.Sleep(50);
        if (!open) throw new InvalidOperationException("clipboard is busy");
        var handed = new List<IntPtr>(2);
        try
        {
            EmptyClipboard();
            // Written directly into the clipboard's memory, avoiding another full-size managed copy.
            handed.Add(Put(CfDibV5, DibV5Header + image.Width * 4L * image.Height, dest => WriteDibV5(image, dest)));
            try { handed.Add(Put(RegisterClipboardFormatW("PNG"), png.Length, dest => png.CopyTo(dest))); }
            catch (Exception e) { log?.Warn("clipboard: the PNG format was not added, the bitmap is there: " + e.Message); }
        }
        finally { CloseClipboard(); }
        _ = ReleaseLocalCopiesAsync(handed, log);
    }

    /// <summary>Puts plain text (CF_UNICODETEXT) on the clipboard: "Copy text" and the colour picker. Any thread.</summary>
    public static void SetText(string text, Core.Diagnostics.ILog? log = null)
    {
        lock (WriteGate) SetTextLocked(text, log);
    }

    private static void SetTextLocked(string text, Core.Diagnostics.ILog? log)
    {
        bool open = false;
        for (int i = 0; i < 10 && !(open = OpenClipboard(IntPtr.Zero)); i++) Thread.Sleep(50);
        if (!open) throw new InvalidOperationException("clipboard is busy");
        var handed = new List<IntPtr>(1);
        try
        {
            EmptyClipboard();
            // UTF-16 with its terminating null, as CF_UNICODETEXT requires.
            handed.Add(Put(CfUnicodeText, (text.Length + 1) * 2L, dest =>
            {
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(text.AsSpan()).CopyTo(dest);
                dest[^2..].Clear();
            }));
        }
        finally { CloseClipboard(); }
        _ = ReleaseLocalCopiesAsync(handed, log);
    }

    private const uint CfUnicodeText = 13;

    /// <summary>
    /// Memory handed to SetClipboardData stays in this process until it opens and closes the clipboard again after
    /// Windows has taken its copy. So this does an empty open/close on a backoff until the handles are freed.
    /// <para>Not GlobalFree, because the system owns the handle once SetClipboardData succeeds; and not
    /// GetClipboardData, which would make a new local copy.</para>
    /// </summary>
    private static async Task ReleaseLocalCopiesAsync(List<IntPtr> handles, Core.Diagnostics.ILog? log)
    {
        foreach (int wait in ReleaseBackoffMs)
        {
            await Task.Delay(wait).ConfigureAwait(false);
            // GlobalSize answers 0 once the system has freed the handle. A later GlobalAlloc elsewhere in the process
            // could reuse the value and read as alive: that costs an extra open and close, nothing more.
            if (handles.TrueForAll(h => GlobalSize(h) == UIntPtr.Zero)) return;
            if (!OpenClipboard(IntPtr.Zero)) continue;   // another app has it open: the next turn tries again
            CloseClipboard();
        }
        if (!handles.TrueForAll(h => GlobalSize(h) == UIntPtr.Zero))
            log?.Debug("clipboard: the local copy of the bitmap was not released yet; the next copy releases it");
    }

    /// <summary>The waits between release attempts; the first is normally enough.</summary>
    private static readonly int[] ReleaseBackoffMs = { 250, 750, 2000, 5000 };

    private delegate void Fill(Span<byte> dest);

    private static unsafe IntPtr Put(uint format, long length, Fill fill)
    {
        if (length > int.MaxValue) throw new InvalidOperationException("image too large for the clipboard");
        IntPtr h = GlobalAlloc(GmemMoveable, (UIntPtr)(ulong)length);
        if (h == IntPtr.Zero) throw new OutOfMemoryException();
        IntPtr p = GlobalLock(h);
        if (p == IntPtr.Zero) { GlobalFree(h); throw new InvalidOperationException("GlobalLock failed"); }
        try { fill(new Span<byte>((void*)p, (int)length)); }
        catch { GlobalUnlock(h); GlobalFree(h); throw; }
        GlobalUnlock(h);
        if (SetClipboardData(format, h) == IntPtr.Zero) { GlobalFree(h); throw new InvalidOperationException("SetClipboardData failed"); }
        return h;
    }

    private const int DibV5Header = 124;

    /// <summary>BITMAPV5HEADER (124 bytes) + bottom-up BGRA rows, BI_BITFIELDS with an alpha mask so Paint/Office honor
    /// transparency, into <paramref name="dest"/>, which is exactly header plus pixels long.</summary>
    private static void WriteDibV5(BgraImage img, Span<byte> dest)
    {
        int stride = img.Width * 4, pixels = stride * img.Height;
        var header = new byte[DibV5Header];
        using (var w = new BinaryWriter(new MemoryStream(header)))
        {
            w.Write(DibV5Header); w.Write(img.Width); w.Write(img.Height); w.Write((ushort)1); w.Write((ushort)32);
            w.Write(3u /*BI_BITFIELDS*/); w.Write((uint)pixels); w.Write(2835); w.Write(2835); w.Write(0u); w.Write(0u);
            w.Write(0x00FF0000u); w.Write(0x0000FF00u); w.Write(0x000000FFu); w.Write(0xFF000000u);
            w.Write(0x73524742u /*'sRGB'*/);
            for (int i = 0; i < 9; i++) w.Write(0);          // CIEXYZTRIPLE
            w.Write(0u); w.Write(0u); w.Write(0u);           // gamma r/g/b
            w.Write(4u /*LCS_GM_IMAGES*/); w.Write(0u); w.Write(0u); w.Write(0u);
        }
        header.CopyTo(dest);
        for (int y = 0; y < img.Height; y++)   // bottom-up rows, already BGRA
            img.Data.AsSpan((img.Height - 1 - y) * stride, stride).CopyTo(dest.Slice(DibV5Header + y * stride, stride));
    }
}
