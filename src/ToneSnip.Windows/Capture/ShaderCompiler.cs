using System.Runtime.InteropServices;

namespace ToneSnip.Windows.Capture;

/// <summary>
/// D3DCompile and D3DDisassemble from the in-box d3dcompiler_47.dll (System32 on every Windows 10 and later), with no
/// NuGet package or Windows SDK. The app never compiles at run time: it embeds the bytecode this produces
/// (Shaders/*.cso). The bytecode test and tools/gpu-tonemap-bench use it to rebuild and check that bytecode.
/// </summary>
public static unsafe class ShaderCompiler
{
    /// <summary>Every compute shader in Shaders/Tonemap.hlsl, by entry point; each is embedded as
    /// <c>Shaders/{entry}.cso</c>.</summary>
    public static readonly string[] EntryPoints = ["CSTonemap", "CSStats", "CSZebra"];

    public const string Profile = "cs_5_0";

    private const uint OptimizationLevel3 = 1 << 15, IeeeStrictness = 1 << 13;

    /// <summary>
    /// Compiles one entry point. Line endings are normalised first, so a Windows checkout with CRLF compiles to the same
    /// bytes. IEEE strictness keeps the NaN and infinity tests the CPU twins make.
    /// </summary>
    public static byte[] Compile(string source, string entry, string profile = Profile)
    {
        byte[] src = System.Text.Encoding.UTF8.GetBytes(Normalise(source));
        fixed (byte* s = src)
        {
            int hr = D3DCompile(s, (nuint)src.Length, "Tonemap.hlsl", IntPtr.Zero, IntPtr.Zero, entry, profile, OptimizationLevel3 | IeeeStrictness, 0, out IntPtr code, out IntPtr errors);
            try
            {
                if (hr < 0)
                {
                    string msg = errors != IntPtr.Zero ? Marshal.PtrToStringAnsi(BlobPointer(errors), (int)BlobSize(errors)) : "";
                    throw new InvalidOperationException($"D3DCompile {entry} failed 0x{hr:X8}: {msg}");
                }
                return BlobBytes(code);
            }
            finally
            {
                if (code != IntPtr.Zero) Marshal.Release(code);
                if (errors != IntPtr.Zero) Marshal.Release(errors);
            }
        }
    }

    /// <summary>The instruction listing of some bytecode, without its comment lines (which name the compiler's
    /// version), so two compiler builds that emit the same program compare equal.</summary>
    public static string Disassemble(byte[] bytecode)
    {
        fixed (byte* b = bytecode)
        {
            int hr = D3DDisassemble(b, (nuint)bytecode.Length, 0, null, out IntPtr text);
            try
            {
                if (hr < 0) throw new InvalidOperationException($"D3DDisassemble failed 0x{hr:X8}");
                string listing = (Marshal.PtrToStringAnsi(BlobPointer(text), (int)BlobSize(text)) ?? "").TrimEnd('\0');
                return string.Join('\n', Normalise(listing).Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            }
            finally { if (text != IntPtr.Zero) Marshal.Release(text); }
        }
    }

    public static string Normalise(string text) => text.Replace("\r\n", "\n");

    private static byte[] BlobBytes(IntPtr blob)
    {
        var bytes = new byte[BlobSize(blob)];
        Marshal.Copy(BlobPointer(blob), bytes, 0, bytes.Length);
        return bytes;
    }

    // ID3DBlob vtable: QueryInterface, AddRef, Release, GetBufferPointer, GetBufferSize.
    private static IntPtr BlobPointer(IntPtr blob) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)(*(*(IntPtr**)blob + 3)))(blob);
    private static nuint BlobSize(IntPtr blob) => ((delegate* unmanaged[Stdcall]<IntPtr, nuint>)(*(*(IntPtr**)blob + 4)))(blob);

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int D3DCompile(byte* srcData, nuint srcDataSize, string sourceName, IntPtr defines, IntPtr include,
        string entryPoint, string target, uint flags1, uint flags2, out IntPtr code, out IntPtr errorMsgs);

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int D3DDisassemble(byte* srcData, nuint srcDataSize, uint flags, string? comments, out IntPtr disassembly);
}
