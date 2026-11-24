using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace Pec.Qc.Core.Pid;

public enum PidKind { Line, Tag, Label, Note }

/// <summary>One thing read from a P&amp;ID: a line number, a tag (equipment, instrument, valve), a short label or a note, with its place on the sheet (reference frame: A3 landscape, from the top left).</summary>
public sealed record PidItem(PidKind Kind, string Text, double X, double Y, bool Vertical)
{
    public string Key => PidReader.Key(Text);
    public string Zone => PidReader.Zone(X, Y);
}

public sealed class PidSheet
{
    public string File { get; set; } = "";
    public int Page { get; set; }
    public List<PidItem> Items { get; } = [];
    public List<string> Warnings { get; } = [];
    public bool HasText => Items.Count > 0;
}

/// <summary>
/// Reads a P&amp;ID PDF's text layer into line numbers, tags, labels and notes with positions. It reads horizontal AND rotated text (line numbers run along the pipes).
/// The same extraction is applied to the redrawn and to the original drawing, so what matters is that it is the same on both, not that it is perfect: everything that is
/// text ends up in one of the four kinds and is compared. Symbols and how the lines are routed are NOT read.
/// </summary>
public static partial class PidReader
{
    public const double RefWidth = 1190.551, RefHeight = 841.889;

    // 2"-CD-60A07900-B400A-N-HS-017    2 1/2"-P-12345-A1A-N
    [GeneratedRegex("""\d+(?:\s?\d/\d)?\s?["”]?\s?-\s?[A-Z0-9]{1,5}(?:\s?-\s?[A-Z0-9]+){3,7}""")]
    private static partial Regex LineNoRx();

    // V-201  PT-1001  XV1001  P-101A  60-V-201  CL150
    [GeneratedRegex(@"(?<![A-Za-z0-9\-])(?:\d{2,3}-)?[A-Z]{1,4}-?\d{2,5}[A-Z]{0,2}(?![A-Za-z0-9\-])")]
    private static partial Regex TagRx();

    [GeneratedRegex("[A-Za-z]{2,}")]
    private static partial Regex WordRx();

    public static string Key(string text) => Regex.Replace((text ?? "").Replace('”', '"').Replace('’', '\'').ToUpperInvariant().TrimStart('/'), @"\s+", "");

    public static string Zone(double x, double y)
    {
        var col = Math.Clamp((int)(x / (RefWidth / 8)), 0, 7);
        var row = Math.Clamp((int)(y / (RefHeight / 6)) + 1, 1, 6);
        return $"{(char)('A' + col)}{row}";
    }

    public static List<PidSheet> Read(string pdfPath)
    {
        if (!string.Equals(Path.GetExtension(pdfPath), ".pdf", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Only .pdf files are read.");
        using var doc = PdfDocument.Open(pdfPath);
        var sheets = new List<PidSheet>();
        for (var i = 1; i <= doc.NumberOfPages; i++) sheets.Add(ReadPage(doc.GetPage(i), pdfPath, i));
        return sheets;
    }

    public sealed record RawLine(string T, double X0, double Y0, double X1, double Y1, bool Vertical);

    public static PidSheet ReadPage(Page page, string file, int pageNo)
    {
        var sh = new PidSheet { File = Path.GetFileName(file), Page = pageNo };
        var lines = TextLines(page);
        if (lines.Count == 0) { sh.Warnings.Add("the page has no text layer (a scan or an image): it cannot be read"); return sh; }
        lines = JoinBubbles(lines, out var bubbleTags);
        sh.Items.AddRange(bubbleTags);
        foreach (var l in lines)
        {
            var rest = l.T;
            var cx = (l.X0 + l.X1) / 2; var cy = (l.Y0 + l.Y1) / 2;
            foreach (Match m in LineNoRx().Matches(l.T)) { sh.Items.Add(new PidItem(PidKind.Line, Normalize(m.Value), cx, cy, l.Vertical)); }
            rest = LineNoRx().Replace(rest, " ");
            foreach (Match m in TagRx().Matches(rest)) sh.Items.Add(new PidItem(PidKind.Tag, m.Value, cx, cy, l.Vertical));
            rest = TagRx().Replace(rest, " ");
            rest = Regex.Replace(rest, @"\s+", " ").Trim(' ', ',', ';', ':', '-', '.');
            if (rest.Length < 2) continue;
            // three or more words: a note; else a short label
            sh.Items.Add(new PidItem(WordRx().Matches(rest).Count >= 3 ? PidKind.Note : PidKind.Label, rest, cx, cy, l.Vertical));
        }
        return sh;
    }

    private static string Normalize(string lineNo) => Regex.Replace(lineNo.Replace('”', '"'), @"\s*-\s*", "-").Trim();

    // an instrument bubble prints the function letters over the loop number: PT / 1001 becomes the tag PT-1001
    private static List<RawLine> JoinBubbles(List<RawLine> lines, out List<PidItem> tags)
    {
        tags = [];
        var used = new HashSet<RawLine>();
        foreach (var top in lines.Where(l => !l.Vertical && Regex.IsMatch(l.T, @"^[A-Z]{2,4}$")))
        {
            var cx = (top.X0 + top.X1) / 2;
            var below = lines.FirstOrDefault(b => !b.Vertical && !used.Contains(b) && Regex.IsMatch(b.T, @"^\d{3,5}[A-Z]?$") && Math.Abs((b.X0 + b.X1) / 2 - cx) < 8 && b.Y0 > top.Y0 && b.Y0 - top.Y0 < 14);
            if (below is null || used.Contains(top)) continue;
            used.Add(top); used.Add(below);
            tags.Add(new PidItem(PidKind.Tag, $"{top.T}-{below.T}", cx, (top.Y0 + below.Y1) / 2, false));
        }
        return lines.Where(l => !used.Contains(l)).ToList();
    }

    // ----------------------------------------------------------------------------- text lines, any of the four directions that matter
    private sealed record Ltr(Letter L, double A0, double A1, double C, double Size);

    public static List<RawLine> TextLines(Page page)
    {
        var sx = RefWidth / page.Width; var sy = RefHeight / page.Height;
        var all = new List<RawLine>();
        // direction of each letter from its own baseline: horizontal left to right, or vertical reading up or down
        static int Dir(Letter l)
        {
            var dx = l.EndBaseLine.X - l.StartBaseLine.X; var dy = l.EndBaseLine.Y - l.StartBaseLine.Y;
            if (Math.Abs(dx) >= Math.Abs(dy)) return dx >= 0 ? 0 : -1;       // 0 = horizontal; -1 = upside down (left out)
            return dy > 0 ? 1 : 2;                                            // 1 = reads upward; 2 = reads downward
        }
        foreach (var grp in page.Letters.Where(l => !string.IsNullOrEmpty(l.Value) && Dir(l) >= 0).GroupBy(Dir))
        {
            var o = grp.Key;
            // a = position along the reading direction, c = position across it (the baseline)
            Ltr Map(Letter l) => o switch
            {
                0 => new Ltr(l, l.StartBaseLine.X, l.EndBaseLine.X, l.StartBaseLine.Y, l.PointSize),
                1 => new Ltr(l, l.StartBaseLine.Y, l.EndBaseLine.Y, l.StartBaseLine.X, l.PointSize),
                _ => new Ltr(l, -l.StartBaseLine.Y, -l.EndBaseLine.Y, l.StartBaseLine.X, l.PointSize),
            };
            var letters = grp.Select(Map).OrderByDescending(x => x.C).ThenBy(x => x.A0).ToList();
            var rows = new List<List<Ltr>>(); var cur = new List<Ltr>(); var sum = 0.0;
            foreach (var l in letters)
            {
                if (cur.Count > 0 && Math.Abs(l.C - sum / cur.Count) > 0.8) { rows.Add([.. cur.OrderBy(x => x.A0)]); cur = []; sum = 0; }
                cur.Add(l); sum += l.C;
            }
            if (cur.Count > 0) rows.Add([.. cur.OrderBy(x => x.A0)]);
            foreach (var row in rows)
            {
                var run = new List<Ltr>();
                foreach (var l in row)
                {
                    if (run.Count > 0)
                    {
                        var gap = l.A0 - run[^1].A1; var size = Math.Max(Math.Max(run[^1].Size, l.Size), 4);
                        if (gap > 0.5 * size || gap < -1.5 * size) { Flush(run, all, page, sx, sy, o != 0); run = []; }
                    }
                    run.Add(l);
                }
                Flush(run, all, page, sx, sy, o != 0);
            }
        }
        return all;
    }

    private static void Flush(List<Ltr> run, List<RawLine> into, Page page, double sx, double sy, bool vertical)
    {
        if (run.Count == 0) return;
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < run.Count; i++)
        {
            if (i > 0 && run[i].A0 - run[i - 1].A1 > 0.15 * Math.Max(Math.Max(run[i - 1].Size, run[i].Size), 4) && !run[i - 1].L.Value.EndsWith(' ') && !run[i].L.Value.StartsWith(' ')) sb.Append(' ');
            sb.Append(run[i].L.Value);
        }
        var text = sb.ToString().Trim();
        if (text.Length == 0) return;
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (var r in run)
        {
            var g = r.L.GlyphRectangle;
            x0 = Math.Min(x0, Math.Min(g.Left, r.L.StartBaseLine.X)); x1 = Math.Max(x1, Math.Max(g.Right, r.L.EndBaseLine.X));
            y0 = Math.Min(y0, g.Bottom); y1 = Math.Max(y1, g.Top);
        }
        // page coordinates run up from the bottom; the reference frame runs down from the top
        into.Add(new RawLine(text, x0 * sx, (page.Height - y1) * sy, x1 * sx, (page.Height - y0) * sy, vertical));
    }
}
