namespace Pec.Qc.Tests;

/// <summary>All data in the tests is synthetic: no client files and no model data in the repository.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pecs3d-" + Guid.NewGuid().ToString("N")[..8]);
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string rel, string text)
    {
        var p = System.IO.Path.Combine(Path, rel);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, text);
        return p;
    }
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

public static class TestPaths
{
    /// <summary>Walk up from the test output folder to find a repository file; empty string when it does not exist.</summary>
    public static string FindUp(string rel)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(System.IO.Path.Combine(d.FullName, rel))) d = d.Parent;
        return d is null ? "" : System.IO.Path.Combine(d.FullName, rel);
    }
}
