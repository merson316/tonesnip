namespace ToneSnip.Core.Geometry;

/// <summary>
/// Where the recording bar goes: centred under the recorded area, or above it when there is no room below, or inside
/// its bottom edge when the area fills its monitor's height. The bar is left out of the capture, so even inside the
/// area it is not in the video; it is only kept out of the way.
/// </summary>
public static class RecordBarLayout
{
    /// <param name="area">The recorded area and <paramref name="monitor"/> its monitor, in physical pixels.</param>
    /// <param name="width">The bar's size in physical pixels.</param>
    /// <param name="gap">The space kept between the bar and the area, and the monitor's edges.</param>
    public static IntRect Place(IntRect area, IntRect monitor, int width, int height, int gap)
    {
        int x = Math.Clamp(area.Left + (area.Width - width) / 2, monitor.Left + gap, Math.Max(monitor.Left + gap, monitor.Right - gap - width));
        int y;
        if (area.Bottom + gap + height <= monitor.Bottom - gap) y = area.Bottom + gap;
        else if (area.Top - gap - height >= monitor.Top + gap) y = area.Top - gap - height;
        else y = Math.Max(monitor.Top + gap, area.Bottom - gap - height);
        return new IntRect(x, y, width, height);
    }
}
