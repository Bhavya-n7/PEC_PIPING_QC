using System.Text.RegularExpressions;

namespace Pec.Qc.Tests;

/// <summary>Text that was saved twice with the wrong encoding shows up as "â€¦" instead of "…" and as empty boxes instead of icons. No source file may contain it.</summary>
public class EncodingTests
{
    private static string Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "src")) && File.Exists(Path.Combine(d.FullName, "DEVELOPMENT.md"))) return d.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    [Fact]
    public void No_source_file_contains_double_encoded_text()
    {
        var bad = new List<string>();
        var rx = new Regex("(â€|Ã[\u0080-\u00BF]|Â[\u0080-\u00BF]|î[\u0080-\u00BF¥¨œ])", RegexOptions.Compiled);
        foreach (var f in Directory.EnumerateFiles(Root(), "*.*", SearchOption.AllDirectories)
                     .Where(f => Path.GetExtension(f) is ".cs" or ".xaml" or ".md" or ".ps1" or ".json" or ".html")
                     .Where(f => !f.Contains(@"\bin\") && !f.Contains(@"\obj\") && !f.Contains(@"\.venv\") && !f.Contains(@"\third-party\") && !f.Contains(@"\dist\") && !f.EndsWith("EncodingTests.cs")))
        {
            var lines = File.ReadAllLines(f);
            for (var i = 0; i < lines.Length; i++) if (rx.IsMatch(lines[i])) { bad.Add($"{Path.GetRelativePath(Root(), f)}:{i + 1}"); break; }
        }
        Assert.True(bad.Count == 0, "Double-encoded characters in: " + string.Join(", ", bad));
    }
}
