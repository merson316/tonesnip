using System.Runtime.InteropServices;

namespace ToneSnip.Windows.Display;

/// <param name="FriendlyName">The monitor's EDID name, or null when Windows has none for this target (a generic PnP
/// monitor, a virtual display, or an EDID without a name).</param>
/// <param name="AdvancedColorBits">DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO's bit field, or null when it could not be read.</param>
public sealed record DisplayInfo(float SdrWhiteNits, uint? AdvancedColorBits, string? FriendlyName = null)
{
    /// <summary>HDR by DisplayConfig alone: advanced colour on, and not an SDR display running Auto Color Management.</summary>
    public bool AdvancedColorEnabled => ToneSnip.Core.Capture.HdrDetection.IsHdr(false, false, AdvancedColorBits);
}

/// <summary>Reads per-monitor SDR white level, advanced-colour (HDR) state and the EDID name via the DisplayConfig API.</summary>
public static partial class DisplayConfigInterop
{
    private const uint QdcOnlyActivePaths = 2;
    private const uint InfoGetSourceName = 1;
    private const uint InfoGetTargetName = 2;
    private const uint InfoGetAdvancedColorInfo = 9;
    private const uint InfoGetSdrWhiteLevel = 11;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathSourceInfo { public Luid adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathTargetInfo
    {
        public Luid adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology; public uint rotation;
        public uint scaling; public uint refreshRateNumerator; public uint refreshRateDenominator; public uint scanLineOrdering;
        public int targetAvailable; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo { public PathSourceInfo sourceInfo; public PathTargetInfo targetInfo; public uint flags; }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct ModeInfo { }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader { public uint type; public uint size; public Luid adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceDeviceName
    {
        public DeviceInfoHeader header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    /// <summary>DISPLAYCONFIG_TARGET_DEVICE_NAME: the 20-byte header, the fixed fields, then two fixed WCHAR buffers;
    /// 420 bytes in all, which the header's size field must match.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetDeviceName
    {
        public DeviceInfoHeader header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SdrWhiteLevel { public DeviceInfoHeader header; public uint sdrWhiteLevel; }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdvancedColorInfo { public DeviceInfoHeader header; public uint value; public uint colorEncoding; public uint bitsPerColorChannel; }

    private const int ErrorInsufficientBuffer = 122;
    [LibraryImport("user32.dll")] private static partial int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [LibraryImport("user32.dll")] private static partial int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] PathInfo[] paths, ref uint numModes, [Out] ModeInfo[] modes, IntPtr currentTopologyId);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName info);   // runtime-marshalled: inline string
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref TargetDeviceName info);   // runtime-marshalled: inline strings
    [LibraryImport("user32.dll")] private static partial int DisplayConfigGetDeviceInfo(ref SdrWhiteLevel info);
    [LibraryImport("user32.dll")] private static partial int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo info);

    /// <summary>Keyed by GDI device name such as \\.\DISPLAY1 (matches DXGI OutputDescription.DeviceName).</summary>
    public static Dictionary<string, DisplayInfo> Query()
    {
        var result = new Dictionary<string, DisplayInfo>(StringComparer.OrdinalIgnoreCase);
        // A topology change between the size query and the config query (likely during a display-change refresh)
        // returns ERROR_INSUFFICIENT_BUFFER, so retry a few times.
        PathInfo[] paths = Array.Empty<PathInfo>();
        ModeInfo[] modes = Array.Empty<ModeInfo>();
        uint numPaths = 0, numModes = 0;
        for (int attempt = 0; ; attempt++)
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out numPaths, out numModes) != 0) return result;
            paths = new PathInfo[numPaths];
            modes = new ModeInfo[numModes];
            int status = QueryDisplayConfig(QdcOnlyActivePaths, ref numPaths, paths, ref numModes, modes, IntPtr.Zero);
            if (status == 0) break;
            if (status != ErrorInsufficientBuffer || attempt == 3) return result;
        }

        for (int i = 0; i < numPaths; i++)
        {
            PathInfo p = paths[i];
            var name = new SourceDeviceName
            {
                header = new DeviceInfoHeader { type = InfoGetSourceName, size = (uint)Marshal.SizeOf<SourceDeviceName>(), adapterId = p.sourceInfo.adapterId, id = p.sourceInfo.id },
            };
            if (DisplayConfigGetDeviceInfo(ref name) != 0 || string.IsNullOrEmpty(name.viewGdiDeviceName)) continue;

            // The friendly name belongs to the path's target and is only real when bit 0 of flags (friendlyNameFromEdid)
            // is set; otherwise the buffer holds a placeholder such as "Generic PnP Monitor".
            string? friendly = null;
            var tn = new TargetDeviceName
            {
                header = new DeviceInfoHeader { type = InfoGetTargetName, size = (uint)Marshal.SizeOf<TargetDeviceName>(), adapterId = p.targetInfo.adapterId, id = p.targetInfo.id },
            };
            if (DisplayConfigGetDeviceInfo(ref tn) == 0 && (tn.flags & 1) != 0 && !string.IsNullOrWhiteSpace(tn.monitorFriendlyDeviceName))
                friendly = tn.monitorFriendlyDeviceName.Trim();

            float sdrWhite = 80f;
            var wl = new SdrWhiteLevel
            {
                header = new DeviceInfoHeader { type = InfoGetSdrWhiteLevel, size = (uint)Marshal.SizeOf<SdrWhiteLevel>(), adapterId = p.targetInfo.adapterId, id = p.targetInfo.id },
            };
            if (DisplayConfigGetDeviceInfo(ref wl) == 0 && wl.sdrWhiteLevel > 0) sdrWhite = wl.sdrWhiteLevel / 1000f * 80f;

            uint? colorBits = null;
            var ac = new AdvancedColorInfo
            {
                header = new DeviceInfoHeader { type = InfoGetAdvancedColorInfo, size = (uint)Marshal.SizeOf<AdvancedColorInfo>(), adapterId = p.targetInfo.adapterId, id = p.targetInfo.id },
            };
            if (DisplayConfigGetDeviceInfo(ref ac) == 0) colorBits = ac.value;   // bit 1 advancedColorEnabled, bit 2 wideColorEnforced

            result[name.viewGdiDeviceName] = new DisplayInfo(sdrWhite, colorBits, friendly);
        }
        return result;
    }
}
