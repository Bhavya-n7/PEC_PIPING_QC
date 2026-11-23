using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace Pec.Qc.Core.Knowledge;

public sealed record ManualChunk(string Doc, int Page, string Text);
public sealed record ManualHit(string Doc, int Page, double Score, string Text);

/// <summary>
/// Local search over the project documents and vendor manuals (PDFs) the user has on this PC or the shared folder. The text is read from the
/// PDFs on this PC and stored only in the assistant's data folder; nothing is copied into the repository.
/// </summary>
public sealed class ManualIndex
{
    private readonly string _file;
    private Bm25<ManualChunk>? _bm25;

    public ManualIndex(string? directory = null)
    {
        var dir = directory ?? Path.Combine(Settings.DataDirectory, "manual-index");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "chunks.jsonl");
    }

    public bool Exists => File.Exists(_file);
    public string FilePath => _file;

    /// <summary>The manuals worth indexing: the user's and reference guides (not install checklists or licence text).</summary>
    public static bool IsUsefulManual(string title) =>
        Regex.IsMatch(title, @"UsersGuide|ReferenceGuide|Reference Guide|Help$", RegexOptions.IgnoreCase) &&
        !Regex.IsMatch(title, @"Install", RegexOptions.IgnoreCase);

    /// <summary>Read the PDFs and write the index. Returns number of chunks.</summary>
    public int Build(IEnumerable<string> pdfFiles, Action<string>? progress = null)
    {
        var count = 0;
        var tmp = _file + ".tmp";
        using (var w = new StreamWriter(tmp, false, new UTF8Encoding(false)))
            foreach (var pdf in pdfFiles.Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
            {
                progress?.Invoke($"Reading {Path.GetFileName(pdf)}");
                if (!pdf.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    var n = Path.GetFileNameWithoutExtension(pdf);
                    foreach (var chunk in Chunk(File.ReadAllText(pdf)))
                    {
                        w.WriteLine(JsonSerializer.Serialize(new ManualChunk(n, 1, chunk)));
                        count++;
                    }
                    continue;
                }
                try
                {
                    using var doc = PdfDocument.Open(pdf);
                    var name = Path.GetFileNameWithoutExtension(pdf);
                    foreach (var page in doc.GetPages())
                        foreach (var chunk in Chunk(page.Text))
                        {
                            w.WriteLine(JsonSerializer.Serialize(new ManualChunk(name, page.Number, chunk)));
                            count++;
                        }
                }
                catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException)
                {
                    progress?.Invoke($"Skipped {Path.GetFileName(pdf)}: {e.Message}");
                }
            }
        File.Move(tmp, _file, true);
        _bm25 = null;
        return count;
    }

    /// <summary>Split a page into pieces of about 900 characters at sentence ends, with a little overlap.</summary>
    public static IEnumerable<string> Chunk(string text, int size = 900, int overlap = 120)
    {
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length == 0) yield break;
        var start = 0;
        while (start < text.Length)
        {
            var end = Math.Min(text.Length, start + size);
            if (end < text.Length)
            {
                var cut = text.LastIndexOfAny(['.', ';', ':'], end - 1, Math.Min(250, end - start));
                if (cut > start + size / 2) end = cut + 1;
            }
            yield return text[start..end].Trim();
            if (end >= text.Length) yield break;
            start = Math.Max(end - overlap, start + 1);
        }
    }

    private Bm25<ManualChunk> Load()
    {
        if (_bm25 is not null) return _bm25;
        var b = new Bm25<ManualChunk>();
        if (File.Exists(_file))
            foreach (var line in File.ReadLines(_file))
                if (JsonSerializer.Deserialize<ManualChunk>(line) is { } c) b.Add(c, c.Text);
        return _bm25 = b;
    }

    public List<ManualHit> Search(string query, int max = 5) =>
        Load().Search(query, max).Select(h => new ManualHit(h.Item.Doc, h.Item.Page, Math.Round(h.Score, 2), h.Item.Text)).ToList();

    /// <summary>Passages that mention "Option Switch - N" (optionally with a position), as the Isometric Drawing Options Reference words it.</summary>
    public List<ManualHit> OptionSwitch(int number, int max = 6)
    {
        var rx = new Regex($@"Option\s+Switch\s*-?\s*{number}(?!\d)", RegexOptions.IgnoreCase);
        var hits = new List<ManualHit>();
        if (!File.Exists(_file)) return hits;
        foreach (var line in File.ReadLines(_file))
        {
            var c = JsonSerializer.Deserialize<ManualChunk>(line);
            if (c is null || !rx.IsMatch(c.Text)) continue;
            hits.Add(new ManualHit(c.Doc, c.Page, c.Doc.Contains("Option", StringComparison.OrdinalIgnoreCase) ? 2 : 1, c.Text));
        }
        return hits.OrderByDescending(h => h.Score).ThenBy(h => h.Page).Take(max).ToList();
    }
}
