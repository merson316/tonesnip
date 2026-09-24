using System.Diagnostics;
using System.Runtime.InteropServices;
using ToneSnip.Core.Capture;
using ToneSnip.Core.Geometry;
using ToneSnip.Core.Imaging;
using ToneSnip.Windows.Interop;

namespace ToneSnip.App;

/// <summary>
/// The self-test's window-capture checks: on each monitor, a red window is put up with a blue one over its middle, and
/// the red one is captured on its own (<see cref="Capture.FrameGrabber.GrabWindow"/>). The covered middle must come
/// back red, and a part nothing covers must match the same part of a grab of every monitor, which on an HDR monitor
/// also checks the window's frame is tonemapped at the monitor's own white. The window's rounded corners must be
/// transparent at the bottom too, over its GDI client area, and stay transparent through the editor's exposure pass and
/// into the HDR file. Both windows are this process's, shown without activation, and closed at once.
/// </summary>
public static partial class SelfTest
{
    /// <summary>Largest per-channel difference allowed between the window's own capture and the screen's crop of an
    /// uncovered part.</summary>
    private const int WindowToleranceLsb = 6;

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr GetProcAddress(IntPtr module, string name);
    // DllImport: the message struct is not one the source generator marshals in this project.
    [DllImport("user32.dll")] private static extern bool PeekMessageW(out Msg msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(in Msg msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(in Msg msg);

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public User32.Point Pt; public uint Private; }

    private const int WsOverlappedWindow = 0x00CF0000, WsPopup = unchecked((int)0x80000000), WsExTopmost = 0x8, WsExNoActivate = 0x08000000;
    private const int SwShowNa = 8;

    private static bool WindowCaptureChecks(Capture.FrameGrabber grabber)
    {
        bool ok = true;
        IntPtr instance = Kernel32.GetModuleHandleW(null);
        IntPtr red = Gdi32.CreateSolidBrush(0x1E3CC8), blue = Gdi32.CreateSolidBrush(0xFF0000);   // COLORREF is 0x00BBGGRR: a mid red, not a saturated one, so a wrong white level shows
        IntPtr proc = GetProcAddress(Kernel32.GetModuleHandleW("user32.dll"), "DefWindowProcW");
        string redClass = "tonesnip-selftest-red", blueClass = "tonesnip-selftest-blue";
        Register(redClass, red);
        Register(blueClass, blue);
        try
        {
            foreach (OutputInfo o in grabber.Outputs())
            {
                var target = new IntRect(o.Left + o.Width / 4, o.Top + o.Height / 4, Math.Min(900, o.Width / 2), Math.Min(600, o.Height / 2));
                var cover = new IntRect(target.Left + target.Width / 3, target.Top + target.Height / 3, target.Width / 3, target.Height / 3);
                IntPtr a = User32.CreateWindowExW(WsExTopmost | WsExNoActivate, redClass, "ToneSnip self-test", WsOverlappedWindow, target.Left, target.Top, target.Width, target.Height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
                IntPtr b = User32.CreateWindowExW(WsExTopmost | WsExNoActivate, blueClass, null, WsPopup, cover.Left, cover.Top, cover.Width, cover.Height, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
                try
                {
                    User32.ShowWindow(a, SwShowNa); User32.ShowWindow(b, SwShowNa);
                    Pump(600);
                    var sw = Stopwatch.StartNew();
                    Capture.WindowGrab? grab = grabber.GrabWindow(a);
                    double tWindow = sw.Elapsed.TotalMilliseconds;
                    if (grab == null) { Console.WriteLine($"{o.DeviceName}: window capture FAILED (fell back; see the log)"); ok = false; continue; }
                    List<Capture.CapturedOutput> all = new();
                    try
                    {
                        sw.Restart();
                        all = grabber.GrabAll();
                        double tAll = sw.Elapsed.TotalMilliseconds;
                        IntRect region = grab.Region;
                        var settings = new Core.Config.SnipSettings();
                        Capture.CaptureResult own = Capture.CaptureResult.Build(grab.Outputs, region, null, grabber, settings);
                        own.ApplyCorners(grab.Corners);
                        Capture.CaptureResult screen = Capture.CaptureResult.Build(all, region, null, grabber, new Core.Config.SnipSettings());
                        // The covered middle, in the window's own capture.
                        (int cx, int cy) = (cover.Left + cover.Width / 2 - region.Left, cover.Top + cover.Height / 2 - region.Top);
                        byte[] mid = Pixel(own.Image, cx, cy);
                        bool covered = Math.Abs(mid[2] - 200) <= WindowToleranceLsb && Math.Abs(mid[1] - 60) <= WindowToleranceLsb && Math.Abs(mid[0] - 30) <= WindowToleranceLsb;
                        // An uncovered point of the client area, a quarter of the way in, in both.
                        (int ux, int uy) = (region.Width / 8, region.Height * 3 / 4);
                        byte[] p = Pixel(own.Image, ux, uy), q = Pixel(screen.Image, ux, uy);
                        int diff = Enumerable.Range(0, 3).Max(i => Math.Abs(p[i] - q[i]));
                        byte cornerAlpha = Pixel(own.Image, 0, 0)[3];
                        // DWM rounds all four corners alike, so the bottom ones, over the GDI client area whose alpha
                        // the capture leaves at zero, must be as transparent as the top ones from the caption.
                        byte bottomAlpha = Pixel(own.Image, 0, region.Height - 1)[3];
                        // The editor's exposure pass writes the crops over the image; the corners must survive it, and
                        // reach the HDR file, which takes its alpha from the image.
                        string exposed = "no HDR crop";
                        bool keptCorners = true;
                        if (own.Crops.Count > 0)
                        {
                            BgraImage? tmp = null;
                            own.Retonemap(own.Image, 1.5f, grabber, ref tmp);
                            byte afterTop = Pixel(own.Image, 0, 0)[3], afterBottom = Pixel(own.Image, 0, region.Height - 1)[3];
                            (HalfImage canvas, _, _) = Output.HdrOutput.BuildCanvas(own, settings, 0xFF0078D4);
                            float hdrAlpha = Core.Color.Transfer.HalfToFloat(canvas.Data[3]);
                            keptCorners = afterTop == cornerAlpha && afterBottom == bottomAlpha && (cornerAlpha != 0 || hdrAlpha == 0f);
                            exposed = $"after exposure top-left alpha {afterTop}, bottom-left {afterBottom}, HDR file top-left alpha {hdrAlpha:F2}";
                        }
                        bool right = covered && diff <= WindowToleranceLsb && region.Width > 0 && (cornerAlpha != 0 || bottomAlpha == 0) && keptCorners;
                        Console.WriteLine($"{o.DeviceName}: window capture {region.Width}x{region.Height} hdr={grab.Output.Hdr != null} in {tWindow:F0} ms (every monitor: {tAll:F0} ms), " +
                                          $"covered middle BGR {mid[0]},{mid[1]},{mid[2]}, uncovered BGR {p[0]},{p[1]},{p[2]} vs screen {q[0]},{q[1]},{q[2]}, top-left alpha {cornerAlpha}, bottom-left alpha {bottomAlpha}, corners {(grab.Corners == null ? "opaque" : "transparent")}, {exposed}{(right ? "" : " FAILED")}");
                        if (!right) ok = false;
                    }
                    finally { grab.Release(); Capture.FrameGrabber.Release(all); }
                }
                finally { User32.DestroyWindow(b); User32.DestroyWindow(a); Pump(50); }
            }
        }
        finally
        {
            User32.UnregisterClassW(redClass, instance); User32.UnregisterClassW(blueClass, instance);
            Gdi32.DeleteObject(red); Gdi32.DeleteObject(blue);
        }
        return ok;

        void Register(string name, IntPtr brush)
        {
            var c = new User32.WndClassEx
            {
                Size = (uint)Marshal.SizeOf<User32.WndClassEx>(), WndProc = proc, Instance = instance, Background = brush,
                ClassName = Marshal.StringToHGlobalUni(name),
            };
            User32.RegisterClassExW(ref c);
        }
    }

    /// <summary>Runs this thread's messages for <paramref name="ms"/> so the test windows paint and DWM composes
    /// them.</summary>
    private static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            while (PeekMessageW(out Msg m, IntPtr.Zero, 0, 0, 1)) { TranslateMessage(m); DispatchMessageW(m); }
            Thread.Sleep(10);
        }
        Dwmapi.DwmFlush();
    }

    private static byte[] Pixel(BgraImage img, int x, int y) => img.Data.AsSpan((y * img.Width + x) * 4, 4).ToArray();
}
