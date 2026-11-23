using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace Pec.Qc.Core.Knowledge;

public sealed record PackSection(string Title, int Page);

public sealed record PackDocument(
    string Id, string Title, string Source, string Markdown, int Pages, int Characters, string Sha256, DateTime Converted,
    IReadOnlyList<PackSection> Sections);

public sealed record PackIndex(int Version, DateTime Updated, IReadOnlyList<PackDocument> Documents);

public sealed record ConvertReport(int Converted, int Unchanged, int Failed, IReadOnlyList<string> Messages, PackIndex Index);

/// <summary>
/// Turns a folder of training and client documents (PDF, Word-exported text, Markdown) into a "knowledge pack":
/// <c>docs\&lt;id&gt;.md</c> (page markers and headings) and <c>pack.json</c> (index with sections). The pack is a plain folder,
/// so it can sit on a server; running the conversion again only redoes documents that changed.
/// </summary>
public static class TrainingPack
{
    public const string IndexFile = "pack.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static ConvertReport Convert(string sourceDir, string packDir, Action<string>? progress = null)
    {
        Directory.CreateDirectory(Path.Combine(packDir, "docs"));
        var old = Load(packDir)?.Documents.ToDictionary(d => d.Id) ?? [];
        var docs = new List<PackDocument>();
        var messages = new List<string>();
        int converted = 0, unchanged = 0, failed = 0;

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*.*", SearchOption.AllDirectories)
                     .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".pdf" or ".md" or ".txt")
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var id = Slug(Path.GetFileNameWithoutExtension(file));
            var hash = Hash(file);
            if (old.TryGetValue(id, out var prev) && prev.Sha256 == hash && File.Exists(Path.Combine(packDir, prev.Markdown)))
            {
                docs.Add(prev); unchanged++; continue;
            }
            try
            {
                progress?.Invoke($"Converting {Path.GetFileName(file)}");
                var (md, pages, sections) = Path.GetExtension(file).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                    ? FromPdf(file) : FromText(file);
                var title = Path.GetFileNameWithoutExtension(file);
                var rel = Path.Combine("docs", id + ".md");
                var header = $"---\ntitle: {title}\nsource: {Path.GetFileName(file)}\npages: {pages}\nconverted: {DateTime.Now:yyyy-MM-dd}\n---\n\n";
                File.WriteAllText(Path.Combine(packDir, rel), header + md, new UTF8Encoding(false));
                docs.Add(new PackDocument(id, title, Path.GetFileName(file), rel.Replace('\\', '/'), pages, md.Length, hash, DateTime.Now, sections));
                converted++;
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException)
            {
                failed++; messages.Add($"{Path.GetFileName(file)}: {e.Message}");
            }
        }
        var index = new PackIndex(1, DateTime.Now, docs);
        File.WriteAllText(Path.Combine(packDir, IndexFile), JsonSerializer.Serialize(index, Json));
        return new ConvertReport(converted, unchanged, failed, messages, index);
    }

    public static PackIndex? Load(string packDir)
    {
        var f = Path.Combine(packDir, IndexFile);
        try { return File.Exists(f) ? JsonSerializer.Deserialize<PackIndex>(File.ReadAllText(f)) : null; }
        catch (JsonException) { return null; }
    }

    // ---- PDF → Markdown

    private sealed record PdfLine(string Text, double Size, bool Bold, double Y);

    /// <summary>
    /// Headings are found by how they look (larger and bold compared with the body text of the document), not by numbering,
    /// so procedure steps such as "4. Select Open" are not mistaken for headings. Running headers and footers are dropped.
    /// </summary>
    private static (string Markdown, int Pages, List<PackSection> Sections) FromPdf(string file)
    {
        using var pdf = PdfDocument.Open(file);
        var pages = pdf.GetPages().Select(pg => (pg.Number, Lines: LinesOf(pg))).ToList();

        // body size = the most common font size; running header/footer = a line that repeats on many pages
        var body = pages.SelectMany(pg => pg.Lines).GroupBy(l => Math.Round(l.Size, 1)).OrderByDescending(g => g.Sum(l => l.Text.Length)).FirstOrDefault()?.Key ?? 10;
        var repeated = pages.SelectMany(pg => pg.Lines.Select(l => Regex.Replace(l.Text, @"\d+", "#")).Distinct())
            .GroupBy(t => t).Where(g => g.Count() > Math.Max(4, pages.Count / 4)).Select(g => g.Key).ToHashSet();

        var sb = new StringBuilder();
        var sections = new List<PackSection>();
        foreach (var (number, lines) in pages)
        {
            sb.AppendLine($"<!-- page {number} -->").AppendLine();
            PdfLine? prev = null;
            foreach (var l in lines)
            {
                var t = l.Text.Trim();
                if (t.Length == 0 || repeated.Contains(Regex.Replace(t, @"\d+", "#")) || Regex.IsMatch(t, @"^(Page )?\d+( of \d+)?$")) continue;
                if (Regex.IsMatch(t, @"\.{4,}")) { prev = l; continue; }                 // table-of-contents line with dotted leader
                var level = l.Size >= body * 1.5 ? 1 : l.Size >= body * 1.22 ? 2 : l.Size >= body * 1.08 && l.Bold ? 3 : 0;
                if (level > 0 && t.Length <= 100 && !t.EndsWith('.') && Regex.IsMatch(t, @"[A-Za-z]{3}"))
                {
                    // a heading that wraps onto a second line: join it
                    if (prev is not null && sb.ToString().EndsWith($"{new string('#', level)} {prev.Text.Trim()}\n\n", StringComparison.Ordinal) && Math.Abs(prev.Size - l.Size) < 0.5 && prev.Y - l.Y < l.Size * 1.8)
                    {
                        var joined = sb.ToString().TrimEnd();
                        sb.Clear().Append(joined).Append(' ').AppendLine(t).AppendLine();
                        sections[^1] = sections[^1] with { Title = sections[^1].Title + " " + t };
                    }
                    else
                    {
                        sb.AppendLine().AppendLine($"{new string('#', level)} {t}").AppendLine();
                        sections.Add(new PackSection(t, number));
                    }
                }
                else
                {
                    if (prev is not null && prev.Y - l.Y > Math.Max(prev.Size, l.Size) * 2.2) sb.AppendLine();     // paragraph gap
                    sb.AppendLine(t);
                }
                prev = l;
            }
            sb.AppendLine();
        }
        return (Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n").Trim() + "\n", pdf.NumberOfPages, sections);
    }

    private static List<PdfLine> LinesOf(UglyToad.PdfPig.Content.Page page)
    {
        var words = page.GetWords().Where(w => w.Text.Trim().Length > 0).ToList();
        var lines = new List<PdfLine>();
        foreach (var row in words.GroupBy(w => Math.Round(w.BoundingBox.Bottom / 2.5)).OrderByDescending(g => g.Key))
        {
            var ws = row.OrderBy(w => w.BoundingBox.Left).ToList();
            var letters = ws.SelectMany(w => w.Letters).ToList();
            if (letters.Count == 0) continue;
            var size = letters.Select(l => (double)l.PointSize).OrderBy(x => x).ElementAt(letters.Count / 2);
            var bold = letters.Count(l => l.Font.IsBold || (l.FontName ?? "").Contains("Bold", StringComparison.OrdinalIgnoreCase)) > letters.Count / 2;
            lines.Add(new PdfLine(string.Join(" ", ws.Select(w => w.Text)), size, bold, ws.Average(w => w.BoundingBox.Bottom)));
        }
        return lines;
    }

    private static (string Markdown, int Pages, List<PackSection> Sections) FromText(string file)
    {
        var text = File.ReadAllText(file);
        var sections = Regex.Matches(text, @"^#{1,3}\s+(.+)$", RegexOptions.Multiline).Select(m => new PackSection(m.Groups[1].Value.Trim(), 1)).ToList();
        return (text.Trim() + "\n", 1, sections);
    }

    // ---- reading parts of a converted document (so a whole document never has to be loaded)

    /// <summary>Text of pages from..to (inclusive) of a document.</summary>
    public static string? ReadPages(string packDir, string id, int from, int to)
    {
        var doc = Load(packDir)?.Documents.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (doc is null) return null;
        var text = File.ReadAllText(Path.Combine(packDir, doc.Markdown));
        var parts = Regex.Split(text, @"<!-- page (\d+) -->");
        var sb = new StringBuilder();
        for (var i = 1; i + 1 < parts.Length; i += 2)
        {
            var n = int.Parse(parts[i]);
            if (n >= from && n <= to) sb.AppendLine($"[page {n}]").AppendLine(parts[i + 1].Trim()).AppendLine();
        }
        return sb.ToString().Trim();
    }

    /// <summary>Text of the first section whose title contains <paramref name="titlePart"/>, up to the next heading of the same or a higher level.</summary>
    public static string? ReadSection(string packDir, string id, string titlePart)
    {
        var doc = Load(packDir)?.Documents.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (doc is null) return null;
        var lines = File.ReadAllText(Path.Combine(packDir, doc.Markdown)).Replace("\r\n", "\n").Split('\n');
        var start = Array.FindIndex(lines, l => l.StartsWith('#') && l.Contains(titlePart, StringComparison.OrdinalIgnoreCase));
        if (start < 0) return null;
        var level = lines[start].TakeWhile(c => c == '#').Count();
        var end = lines.Length;
        for (var i = start + 1; i < lines.Length; i++)
            if (lines[i].StartsWith('#') && lines[i].TakeWhile(c => c == '#').Count() <= level) { end = i; break; }
        var text = string.Join("\n", lines[start..end]);
        return Regex.Replace(text, @"<!-- page (\d+) -->", "[page $1]").Trim();
    }

    private static string Slug(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-') is { Length: > 0 } x ? (x.Length > 40 ? x[..40].TrimEnd('-') : x) : "doc";

    private static string Hash(string file)
    {
        using var s = File.OpenRead(file);
        return Convert(SHA256.HashData(s));
    }

    private static string Convert(byte[] b) => System.Convert.ToHexString(b)[..16].ToLowerInvariant();
}
