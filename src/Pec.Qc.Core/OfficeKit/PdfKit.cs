using System.Text;
using System.Text.Json;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace Pec.Qc.Core.OfficeKit;

/// <summary>Read, create, merge and split PDF files without Acrobat (PdfPig reads, PDFsharp writes; both open source).</summary>
public static class PdfKit
{
    public static object Read(string path, string? pages, int maxChars)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("File not found: " + path);
        maxChars = Math.Clamp(maxChars, 500, 300_000);
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
        var total = pdf.NumberOfPages;
        var wanted = ParsePages(pages, total);
        var sb = new StringBuilder();
        var read = new List<int>();
        foreach (var n in wanted)
        {
            var page = pdf.GetPage(n);
            var text = UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(page);
            var block = $"--- page {n} ---\n{text}\n";
            if (sb.Length + block.Length > maxChars) { sb.Append(block[..Math.Max(0, maxChars - sb.Length)]); read.Add(n); break; }
            sb.Append(block); read.Add(n);
        }
        var empty = sb.ToString().Replace("---", "").Where(char.IsLetterOrDigit).Count() < 20;
        return new
        {
            pages = total, readPages = read, truncated = read.Count < wanted.Count || sb.Length >= maxChars, text = sb.ToString(),
            note = empty ? "Almost no text was found: this looks like a scanned PDF (images). Convert the pages to images and read them with the Read tool, or run OCR (for example Tesseract if installed)." : null,
        };
    }

    public static List<int> ParsePages(string? spec, int total)
    {
        var list = new List<int>();
        if (string.IsNullOrWhiteSpace(spec)) { for (var i = 1; i <= total; i++) list.Add(i); return list; }
        foreach (var part in spec.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ab = part.Split('-', 2);
            if (!int.TryParse(ab[0], out var a)) continue;
            var b = ab.Length == 2 && int.TryParse(ab[1], out var bb) ? bb : a;
            for (var i = Math.Max(1, a); i <= Math.Min(total, b); i++) if (!list.Contains(i)) list.Add(i);
        }
        if (list.Count == 0) throw new ArgumentException($"No page of the document matches '{spec}' (it has {total} pages).");
        return list;
    }

    /// <summary>Blocks as JSON (same shape as word_write): heading, paragraph, bullets, numbered, table, pagebreak. A4, wraps text, adds pages. Standard Latin text (Helvetica).</summary>
    public static string Create(string path, string blocksJson, bool overwrite, string? title)
    {
        if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) path += ".pdf";
        if (File.Exists(path) && !overwrite) throw new IOException($"{path} already exists: pass overwrite=true to replace it.");
        using var doc = JsonDocument.Parse(blocksJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new ArgumentException("'blocks' must be a JSON array of {type, ...}.");
        var builder = new PdfDocumentBuilder();
        if (!string.IsNullOrWhiteSpace(title)) builder.DocumentInformation.Title = title;
        var w = new Writer(builder);
        foreach (var b in doc.RootElement.EnumerateArray())
        {
            var type = (ExcelKit.Str(b, "type") ?? "paragraph").ToLowerInvariant();
            switch (type)
            {
                case "heading":
                {
                    var level = b.TryGetProperty("level", out var lv) && lv.ValueKind == JsonValueKind.Number ? Math.Clamp(lv.GetInt32(), 1, 3) : 1;
                    w.Text(ExcelKit.Str(b, "text") ?? "", level switch { 1 => 20, 2 => 16, _ => 13 }, true, space: 8);
                    break;
                }
                case "paragraph": w.Text(ExcelKit.Str(b, "text") ?? "", 11, ExcelKit.Bool(b, "bold") == true, space: 4); break;
                case "bullets": foreach (var it in b.GetProperty("items").EnumerateArray()) w.Text("-  " + it.GetString(), 11, false, indent: 14, space: 2); break;
                case "numbered": { var n = 0; foreach (var it in b.GetProperty("items").EnumerateArray()) w.Text(++n + ".  " + it.GetString(), 11, false, indent: 14, space: 2); break; }
                case "table": w.Table(b.GetProperty("rows").EnumerateArray().Select(r => r.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : c.ToString()).ToList()).ToList(), ExcelKit.Bool(b, "header") != false); break;
                case "pagebreak": w.NewPage(); break;
                default: throw new ArgumentException("unknown block type '" + type + "'. Known: heading, paragraph, bullets, numbered, table, pagebreak.");
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, builder.Build());
        return path;
    }

    private sealed class Writer
    {
        private readonly PdfDocumentBuilder _b;
        private readonly PdfDocumentBuilder.AddedFont _regular, _bold;
        private PdfPageBuilder _page = null!;
        private double _y;                       // distance from the top
        private const double Margin = 56, PageW = 595, PageH = 842;
        public Writer(PdfDocumentBuilder b)
        {
            _b = b;
            _regular = b.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
            _bold = b.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.HelveticaBold);
            NewPage();
        }

        public void NewPage() { _page = _b.AddPage(PageSize.A4); _y = Margin; }

        private PdfDocumentBuilder.AddedFont F(bool bold) => bold ? _bold : _regular;
        private double Width => PageW - 2 * Margin;

        /// <summary>Only characters the standard fonts have: the rest become safe look-alikes or ?.</summary>
        internal static string Clean(string? t)
        {
            var sb = new StringBuilder();
            foreach (var c in t ?? "")
            {
                if (c == '\r') continue;
                sb.Append(c switch
                {
                    '–' or '—' or '−' => '-',
                    '‘' or '’' => '\'',
                    '“' or '”' => '"',
                    '•' => '-',
                    '→' => '>',
                    '\t' => ' ',
                    _ => (c >= ' ' && c <= '~') || (c >= '¡' && c <= 'ÿ') || c == '\n' ? c : '?',
                });
            }
            return sb.ToString();
        }

        private double Measure(string text, double size, bool bold)
        {
            if (text.Length == 0) return 0;
            var letters = _page.MeasureText(text, size, new PdfPoint(0, 0), F(bold));
            return letters.Count == 0 ? 0 : (double)(letters[^1].EndBaseLine.X - letters[0].StartBaseLine.X);
        }

        private List<string> Wrap(string text, double size, bool bold, double max)
        {
            var result = new List<string>();
            foreach (var para in Clean(text).Split('\n'))
            {
                var cur = new StringBuilder();
                foreach (var word in para.Split(' '))
                {
                    var trial = cur.Length == 0 ? word : cur + " " + word;
                    if (cur.Length == 0 || Measure(trial, size, bold) <= max) { cur.Clear(); cur.Append(trial); }
                    else { result.Add(cur.ToString()); cur.Clear(); cur.Append(word); }
                }
                result.Add(cur.ToString());
            }
            return result;
        }

        public void Text(string text, double size, bool bold, double indent = 0, double space = 4)
        {
            var lineH = size * 1.35;
            foreach (var line in Wrap(text, size, bold, Width - indent))
            {
                if (_y + lineH > PageH - Margin) NewPage();
                if (line.Length > 0) _page.AddText(line, size, new PdfPoint(Margin + indent, PageH - _y - size), F(bold));
                _y += lineH;
            }
            _y += space;
        }

        public void Table(List<List<string>> rows, bool header)
        {
            if (rows.Count == 0) return;
            var cols = rows.Max(r => r.Count);
            var cw = Width / cols;
            for (var ri = 0; ri < rows.Count; ri++)
            {
                var isHead = header && ri == 0;
                var cells = Enumerable.Range(0, cols).Select(c => Wrap(c < rows[ri].Count ? rows[ri][c] : "", 10, isHead, cw - 8)).ToList();
                var h = Math.Max(1, cells.Max(c => c.Count)) * 13.5 + 6;
                if (_y + h > PageH - Margin) NewPage();
                for (var c = 0; c < cols; c++)
                {
                    var x = Margin + c * cw; var yb = PageH - _y - h;
                    if (isHead) { _page.SetTextAndFillColor(220, 230, 245); _page.DrawRectangle(new PdfPoint(x, yb), cw, h, 0.1, true); _page.SetTextAndFillColor(0, 0, 0); _page.ResetColor(); }
                    _page.SetStrokeColor(128, 128, 128);
                    _page.DrawRectangle(new PdfPoint(x, yb), cw, h, 0.5, false);
                    _page.ResetColor();
                    var ly = _y + 3;
                    foreach (var line in cells[c]) { if (line.Length > 0) _page.AddText(line, 10, new PdfPoint(x + 4, PageH - ly - 10), F(isHead)); ly += 13.5; }
                }
                _y += h;
            }
            _y += 8;
        }
    }

    /// <summary>Merges the files (all pages, in order) into a new PDF.</summary>
    public static (string Saved, int Pages) Merge(IReadOnlyList<string> inputs, string output, bool overwrite)
    {
        if (inputs.Count < 2) throw new ArgumentException("Give at least two PDF files to merge.");
        if (!output.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) output += ".pdf";
        if (File.Exists(output) && !overwrite) throw new IOException($"{output} already exists: pass overwrite=true.");
        foreach (var f in inputs) if (!File.Exists(f)) throw new FileNotFoundException("File not found: " + f);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllBytes(output, PdfMerger.Merge(inputs.Select(File.ReadAllBytes).ToArray()));
        return (output, PageCount(output));
    }

    /// <summary>Keeps only the listed pages (for example "1-3,7") in a new PDF.</summary>
    public static (string Saved, int Pages) Extract(string input, string pages, string output, bool overwrite)
    {
        if (!File.Exists(input)) throw new FileNotFoundException("File not found: " + input);
        if (!output.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) output += ".pdf";
        if (File.Exists(output) && !overwrite) throw new IOException($"{output} already exists: pass overwrite=true.");
        var keep = ParsePages(pages, PageCount(input));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllBytes(output, PdfMerger.Merge(new[] { File.ReadAllBytes(input) }, new IReadOnlyList<int>[] { keep }));
        return (output, keep.Count);
    }

    private static int PageCount(string path) { using var d = UglyToad.PdfPig.PdfDocument.Open(path); return d.NumberOfPages; }
}
