using System.Security.Cryptography;
using System.Text;
using ToneSnip.Windows.Capture;
using Xunit;

namespace ToneSnip.Core.Tests.Hdr;

/// <summary>
/// The app embeds precompiled bytecode for Shaders/Tonemap.hlsl rather than compiling at run time, so an edit to the
/// source that is not followed by a rebuild of the bytecode would ship the old shader. To rebuild it, on Windows:
/// <c>TONESNIP_WRITE_SHADERS=1 dotnet test tests/ToneSnip.Core.Tests --filter ShaderBytecode</c>, then commit the .cso
/// files and Tonemap.hlsl.sha256.
/// </summary>
public class ShaderBytecodeTests
{
    private const string Rebuild = "rebuild it on Windows with TONESNIP_WRITE_SHADERS=1 dotnet test tests/ToneSnip.Core.Tests --filter ShaderBytecode";

    private static string ShaderDir()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "ToneSnip.sln"))) return Path.Combine(d.FullName, "src", "ToneSnip.Windows", "Capture", "Shaders");
        throw new DirectoryNotFoundException("the repository root (ToneSnip.sln) is not above " + AppContext.BaseDirectory);
    }

    private static string Source() => File.ReadAllText(Path.Combine(ShaderDir(), "Tonemap.hlsl"));

    /// <summary>The source hash is over LF line endings, so a CRLF checkout agrees with the committed one.</summary>
    private static string SourceHash() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ShaderCompiler.Normalise(Source()))));

    private static bool Writing => Environment.GetEnvironmentVariable("TONESNIP_WRITE_SHADERS") == "1";

    /// <summary>Runs everywhere, CI's Linux leg included: the bytecode was last built from this exact source.</summary>
    [Fact]
    public void Bytecode_was_built_from_the_current_source()
    {
        if (Writing) return;   // the Windows test below writes the hash
        string recorded = File.ReadAllText(Path.Combine(ShaderDir(), "Tonemap.hlsl.sha256")).Trim();
        Assert.True(recorded == SourceHash(), "Tonemap.hlsl changed since its bytecode was built; " + Rebuild);
        foreach (string entry in ShaderCompiler.EntryPoints)
            Assert.True(File.Exists(Path.Combine(ShaderDir(), entry + ".cso")), $"{entry}.cso is missing; " + Rebuild);
    }

    /// <summary>
    /// Windows only (d3dcompiler_47.dll): compiling the source again gives the committed program. Byte-identical on the
    /// machine that built it; another Windows build's compiler may stamp different bytes around the same instructions,
    /// so the listings are compared when the bytes are not.
    /// </summary>
    [Fact]
    public void Committed_bytecode_matches_a_fresh_compile()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "d3dcompiler_47.dll is a Windows component");
        string source = Source(), dir = ShaderDir();
        foreach (string entry in ShaderCompiler.EntryPoints)
        {
            byte[] fresh = ShaderCompiler.Compile(source, entry);
            string path = Path.Combine(dir, entry + ".cso");
            if (Writing) { File.WriteAllBytes(path, fresh); continue; }
            byte[] committed = File.ReadAllBytes(path);
            if (committed.AsSpan().SequenceEqual(fresh)) continue;
            Assert.True(ShaderCompiler.Disassemble(committed) == ShaderCompiler.Disassemble(fresh), $"{entry}.cso is not what Tonemap.hlsl compiles to; " + Rebuild);
        }
        if (Writing) File.WriteAllText(Path.Combine(dir, "Tonemap.hlsl.sha256"), SourceHash() + "\n");
    }
}
