using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Pec.Qc.Core.Sources;

public sealed record OfficeTextResult(string Kind, string Text, bool Truncated, IReadOnlyList<string> Sheets);

/// <summary>
/// Reads the text of Word (.docx), Excel (.xlsx / .xlsm) and PowerPoint (.pptx) files and of delimited text (.csv / .tsv) without Office installed:
/// the Open XML formats are zip files of XML. Used for files a user attaches in the chat, which Claude cannot open directly.
/// </summary>
public static class OfficeText
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PR = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static readonly string[] Supported = [".docx", ".xlsx", ".xlsm", ".pptx", ".csv", ".tsv"];

    public static OfficeTextResult Read(string path, string? sheet = null, int maxChars = 30000, int maxRows = 200)
    {
        maxChars = Math.Clamp(maxChars, 500, 200_000); maxRows = Math.Clamp(maxRows, 1, 5000);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (!File.Exists(path)) throw new FileNotFoundException("File not found: " + path);
        try
        {
            return ext switch
            {
                ".docx" => Cap("Word document", Docx(path), maxChars, []),
                ".pptx" => Cap("PowerPoint presentation", Pptx(path), maxChars, []),
                ".xlsx" or ".xlsm" => Xlsx(path, sheet, maxChars, maxRows),
                ".csv" or ".tsv" => Delimited(path, ext == ".tsv" ? '\t' : ',', maxChars, maxRows),
                ".doc" or ".xls" or ".ppt" => throw new NotSupportedException($"{ext} is the old Office format: save it as {ext}x (docx, xlsx, pptx) and attach it again."),
                _ => throw new NotSupportedException($"Cannot read '{ext}' files here. Supported: {string.Join(", ", Supported)}."),
            };
        }
        catch (InvalidDataException) { throw new InvalidDataException("The file is not a valid Office file (or it is password protected)."); }
    }

    private static OfficeTextResult Cap(string kind, string text, int maxChars, IReadOnlyList<string> sheets) =>
        text.Length <= maxChars ? new(kind, text, false, sheets) : new(kind, text[..maxChars] + $"\n… (cut at {maxChars:N0} characters)", true, sheets);

    private static XDocument Load(ZipArchiveEntry e) { using var s = e.Open(); return XDocument.Load(s); }

    // ------------------------------------------------------------------ Word

    private static string Docx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("no document.xml");
        var body = Load(entry).Descendants(W + "body").FirstOrDefault() ?? throw new InvalidDataException("no body");
        var sb = new StringBuilder();
        foreach (var el in body.Elements())
        {
            if (el.Name == W + "p") sb.AppendLine(Paragraph(el));
            else if (el.Name == W + "tbl")
                foreach (var row in el.Elements(W + "tr"))
                    sb.AppendLine(string.Join(" | ", row.Elements(W + "tc").Select(c => string.Join(" ", c.Elements(W + "p").Select(Paragraph)).Trim())));
        }
        return sb.ToString().TrimEnd();
    }

    private static string Paragraph(XElement p)
    {
        var sb = new StringBuilder();
        foreach (var n in p.Descendants())
        {
            if (n.Name == W + "t") sb.Append(n.Value);
            else if (n.Name == W + "tab") sb.Append('\t');
            else if (n.Name == W + "br" || n.Name == W + "cr") sb.Append('\n');
        }
        var style = p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val")?.Value ?? "";
        var text = sb.ToString();
        return style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && text.Length > 0 ? "# " + text : text;
    }

    // ------------------------------------------------------------------ PowerPoint

    private static string Pptx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var slides = zip.Entries
            .Select(e => (e, m: System.Text.RegularExpressions.Regex.Match(e.FullName, @"^ppt/slides/slide(\d+)\.xml$")))
            .Where(x => x.m.Success).OrderBy(x => int.Parse(x.m.Groups[1].Value)).ToList();
        var sb = new StringBuilder();
        foreach (var (e, m) in slides)
        {
            sb.AppendLine($"--- Slide {m.Groups[1].Value} ---");
            foreach (var para in Load(e).Descendants(A + "p"))
            {
                var t = string.Concat(para.Descendants(A + "t").Select(x => x.Value));
                if (t.Length > 0) sb.AppendLine(t);
            }
        }
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------ Excel

    private static OfficeTextResult Xlsx(string path, string? wanted, int maxChars, int maxRows)
    {
        using var zip = ZipFile.OpenRead(path);
        var wb = Load(zip.GetEntry("xl/workbook.xml") ?? throw new InvalidDataException("no workbook.xml"));
        var rels = zip.GetEntry("xl/_rels/workbook.xml.rels") is { } re
            ? Load(re).Descendants(PR + "Relationship").ToDictionary(x => (string)x.Attribute("Id")!, x => (string)x.Attribute("Target")!)
            : new Dictionary<string, string>();
        var shared = zip.GetEntry("xl/sharedStrings.xml") is { } ss
            ? Load(ss).Descendants(S + "si").Select(si => string.Concat(si.Descendants(S + "t").Select(t => t.Value))).ToList()
            : new List<string>();
        var sheets = wb.Descendants(S + "sheet").Select((x, i) => (Name: (string)x.Attribute("name")!, Rid: (string?)x.Attribute(R + "id"), Index: i + 1)).ToList();
        var names = sheets.Select(s => s.Name).ToList();

        var pick = sheets;
        if (!string.IsNullOrWhiteSpace(wanted))
        {
            pick = sheets.Where(s => s.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase) || s.Index.ToString() == wanted).ToList();
            if (pick.Count == 0) throw new ArgumentException($"No sheet '{wanted}'. Sheets: {string.Join(", ", names)}.");
        }

        var sb = new StringBuilder(); var truncated = false;
        foreach (var sh in pick)
        {
            var target = sh.Rid is not null && rels.TryGetValue(sh.Rid, out var t) ? t : $"worksheets/sheet{sh.Index}.xml";
            target = target.TrimStart('/'); if (!target.StartsWith("xl/")) target = "xl/" + target;
            var entry = zip.GetEntry(target); if (entry is null) continue;
            sb.AppendLine($"=== Sheet: {sh.Name} ===");
            var rows = 0;
            foreach (var row in Load(entry).Descendants(S + "row"))
            {
                if (rows >= maxRows) { sb.AppendLine($"… (more rows; showing the first {maxRows}. Ask for a sheet or use maxRows.)"); truncated = true; break; }
                var cells = new SortedDictionary<int, string>();
                foreach (var c in row.Elements(S + "c"))
                {
                    var col = ColumnIndex((string?)c.Attribute("r") ?? "");
                    var type = (string?)c.Attribute("t");
                    var v = (string?)c.Element(S + "v");
                    string val = type switch
                    {
                        "s" when int.TryParse(v, out var si) && si < shared.Count => shared[si],
                        "inlineStr" => string.Concat(c.Descendants(S + "t").Select(x => x.Value)),
                        "b" => v == "1" ? "TRUE" : "FALSE",
                        _ => v ?? "",
                    };
                    if (val.Length > 0) cells[col] = val.Replace('\t', ' ').Replace('\n', ' ');
                }
                if (cells.Count == 0) continue;
                var max = cells.Keys.Max();
                sb.AppendLine(string.Join("\t", Enumerable.Range(1, max).Select(i => cells.TryGetValue(i, out var s2) ? s2 : "")));
                rows++;
            }
        }
        var r = Cap("Excel workbook", sb.ToString().TrimEnd(), maxChars, names);
        return truncated && !r.Truncated ? r with { Truncated = true } : r;
    }

    private static int ColumnIndex(string cellRef)
    {
        var n = 0;
        foreach (var ch in cellRef) { if (!char.IsLetter(ch)) break; n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1); }
        return Math.Max(n, 1);
    }

    // ------------------------------------------------------------------ CSV / TSV

    private static OfficeTextResult Delimited(string path, char sep, int maxChars, int maxRows)
    {
        var lines = new List<string>(); var truncated = false;
        foreach (var line in File.ReadLines(path))
        {
            if (lines.Count >= maxRows) { truncated = true; break; }
            lines.Add(sep == ',' ? line : line);
        }
        var text = string.Join("\n", lines) + (truncated ? $"\n… (more rows; showing the first {maxRows})" : "");
        var r = Cap(sep == '\t' ? "TSV table" : "CSV table", text, maxChars, []);
        return truncated && !r.Truncated ? r with { Truncated = true } : r;
    }
}
