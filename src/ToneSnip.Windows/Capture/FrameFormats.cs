using Vortice.DXGI;

namespace ToneSnip.Windows.Capture;

public static class FrameFormats
{
    public static int QuarterTurns(ModeRotation r) => r switch
    {
        ModeRotation.Rotate90 => 1, ModeRotation.Rotate180 => 2, ModeRotation.Rotate270 => 3, _ => 0,
    };
}
