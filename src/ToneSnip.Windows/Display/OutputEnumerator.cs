using Vortice.DXGI;

namespace ToneSnip.Windows.Display;

/// <summary>One monitor as enumerated through DXGI. Plain data: the DXGI objects it was read from are released during
/// enumeration, since the capture takes a monitor by its HMONITOR.</summary>
public sealed class OutputHandle
{
    /// <summary>The HMONITOR, which Windows.Graphics.Capture takes a monitor by.</summary>
    public required IntPtr Monitor { get; init; }
    public required string AdapterName { get; init; }
    public required string DeviceName { get; init; }
    /// <summary>The monitor's EDID name, when Windows has one; the GDI DeviceName is the key everything uses.</summary>
    public string? FriendlyName { get; init; }
    public required int Left { get; init; }
    public required int Top { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required ModeRotation Rotation { get; init; }
    public required ColorSpaceType ColorSpace { get; init; }
    public required float MaxLuminance { get; init; }
    public required bool Hdr { get; init; }
    /// <summary>Settable: the SDR content brightness slider changes it without a display change, so each grab re-reads it
    /// (<c>ScreenCapture.RefreshWhiteLevels</c>).</summary>
    public required float SdrWhiteNits { get; set; }

    public override string ToString()
        => $"{DeviceName}{(FriendlyName == null ? "" : $" ({FriendlyName})")} on {AdapterName}: {Width}x{Height} at ({Left},{Top}) hdr={Hdr} colorSpace={ColorSpace} sdrWhite={SdrWhiteNits:F0}nits peak={MaxLuminance:F0}nits rotation={Rotation}";
}

public static class OutputEnumerator
{
    /// <summary>The monitors attached to the desktop, on every hardware adapter. Each adapter and output is released as
    /// soon as it has been read, so nothing DXGI-side stays alive between display changes.</summary>
    public static List<OutputHandle> Enumerate(IReadOnlyDictionary<string, DisplayInfo> displays, Action<string> log)
    {
        var outputs = new List<OutputHandle>();
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
        {
            using IDXGIAdapter1 owned = adapter;   // released after each pass, whether it had outputs or not
            AdapterDescription1 ad = adapter.Description1;
            if ((ad.Flags & AdapterFlags.Software) != 0)
            {
                log($"skipping software adapter {ad.Description}");
                continue;
            }
            for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput output).Success; o++)
            {
                OutputDescription1 d;
                using (IDXGIOutput6? out6 = output.QueryInterfaceOrNull<IDXGIOutput6>())
                {
                    output.Dispose();
                    if (out6 == null) { log($"adapter {ad.Description} output {o}: no IDXGIOutput6, skipped"); continue; }
                    d = out6.Description1;
                }
                if (!d.AttachedToDesktop) continue;
                displays.TryGetValue(d.DeviceName, out DisplayInfo? info);
                bool hdr = ToneSnip.Core.Capture.HdrDetection.IsHdr(d.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020,
                                                                   d.ColorSpace == ColorSpaceType.RgbFullG10NoneP709, info?.AdvancedColorBits);
                outputs.Add(new OutputHandle
                {
                    Monitor = d.Monitor,
                    AdapterName = ad.Description,
                    DeviceName = d.DeviceName,
                    FriendlyName = info?.FriendlyName,
                    Left = d.DesktopCoordinates.Left,
                    Top = d.DesktopCoordinates.Top,
                    Width = d.DesktopCoordinates.Right - d.DesktopCoordinates.Left,
                    Height = d.DesktopCoordinates.Bottom - d.DesktopCoordinates.Top,
                    Rotation = d.Rotation,
                    ColorSpace = d.ColorSpace,
                    MaxLuminance = d.MaxLuminance > 0f ? d.MaxLuminance : 1000f,
                    Hdr = hdr,
                    SdrWhiteNits = info?.SdrWhiteNits ?? 80f,
                });
            }
        }
        return outputs;
    }
}
