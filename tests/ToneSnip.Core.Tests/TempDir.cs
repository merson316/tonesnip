namespace ToneSnip.Core.Tests;

/// <summary>
/// A new folder under the system temp folder, deleted with everything in it on dispose. A test that writes files
/// writes them here, so it leaves nothing behind: not its settings file, not the .bad copy or .tmp file the loader
/// and saver make beside it, and not when an assertion fails half way.
/// </summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("tonesnip-test-").FullName;

    /// <summary>The path of <paramref name="name"/> in the folder.</summary>
    public string Child(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>Writes <paramref name="content"/> to <paramref name="name"/> in the folder and returns its path.</summary>
    public string Write(string name, string content)
    {
        string path = Child(name);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
