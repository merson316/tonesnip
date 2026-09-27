using ToneSnip.Core.Diagnostics;
using ToneSnip.Core.Imaging;
using ToneSnip.Windows.Capture;
using Vortice.Direct3D;
using Vortice.Direct3D11;

namespace ToneSnip.Windows.Tests;

/// <summary>
/// The GPU tonemap's shaders on WARP, Windows' software rasteriser: every Windows install has it, CI's runners
/// included, so the GPU path is tested without a graphics card. Its pow and exp2 are not a vendor's, which is why
/// tools/gpu-tonemap-bench also checks every real adapter; the ≤ 1 LSB bound held on all of them.
/// <para>One device per test class (xunit's class fixture), used under <see cref="Gate"/> as the capture's is.</para>
/// </summary>
public sealed class Warp : IDisposable
{
    public object Gate { get; } = new();
    public ID3D11Device Device { get; }
    public GpuTonemapper Gpu { get; }
    public TestLog Log { get; } = new();

    public Warp()
    {
        D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, [FeatureLevel.Level_11_0], out ID3D11Device? device).CheckError();
        Device = device!;
        lock (Gate) Gpu = GpuTonemapper.Create(Device, Gate, Log);
    }

    /// <summary>A frame on the device holding <paramref name="img"/>'s pixels, uploaded as the harnesses upload theirs.</summary>
    public GpuHdrFrame Upload(HalfImage img)
        => Gpu.Upload(img.Width, img.Height, (y, row) => img.Data.AsSpan(y * img.Width * 4, img.Width * 4).CopyTo(row));

    public void Dispose()
    {
        lock (Gate) Gpu.Release();
        Device.Dispose();
    }
}

/// <summary>Keeps what was logged, so a test can say a path stayed quiet.</summary>
public sealed class TestLog : ILog
{
    private readonly List<string> _warnings = new();

    public IReadOnlyList<string> Warnings { get { lock (_warnings) return _warnings.ToList(); } }

    public void Debug(string message) { }
    public void Info(string message) { }
    public void Warn(string message) { lock (_warnings) _warnings.Add(message); }
    public void Error(string message) { lock (_warnings) _warnings.Add(message); }
}
