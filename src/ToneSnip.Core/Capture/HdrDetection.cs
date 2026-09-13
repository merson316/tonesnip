namespace ToneSnip.Core.Capture;

/// <summary>Whether a monitor is showing HDR, from what DXGI and DisplayConfig report about it.</summary>
public static class HdrDetection
{
    private const uint AdvancedColorEnabled = 0x2, WideColorEnforced = 0x4;

    /// <param name="pqColorSpace">DXGI reports the output as RGB_FULL_G2084_NONE_P2020: HDR is on.</param>
    /// <param name="linearColorSpace">DXGI reports the output as RGB_FULL_G10_NONE_P709.</param>
    /// <param name="advancedColorBits">The bit field of DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO, or null when it could not
    /// be read. Bit 1 is advancedColorEnabled, which Windows 11 also sets for an SDR display running Auto Color
    /// Management; bit 2, wideColorEnforced, is what marks that case.</param>
    public static bool IsHdr(bool pqColorSpace, bool linearColorSpace, uint? advancedColorBits)
    {
        if (pqColorSpace) return true;
        if (advancedColorBits is not uint bits) return linearColorSpace;
        if ((bits & WideColorEnforced) != 0) return false;
        return linearColorSpace || (bits & AdvancedColorEnabled) != 0;
    }
}
