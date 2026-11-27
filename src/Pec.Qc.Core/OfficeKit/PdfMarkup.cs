using System.Globalization;
using System.Text;
using System.Text.Json;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Advanced;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics;

namespace Pec.Qc.Core.OfficeKit;

/// <summary>
/// QC markup on an existing PDF as real, editable annotations (Bluebeam / Acrobat): revision clouds, yellow highlights, small boxed comments with a leader arrow, and a legend.
/// Coordinates are PDF points with the origin at the bottom left of the page (the same space as <see cref="Find"/> returns). Comment boxes are placed automatically in free space:
/// not on text, not on linework, not on each other.
/// </summary>
public static class PdfMarkup
{
    public sealed record Box(double X0, double Y0, double X1, double Y1)
    {
        public double W => X1 - X0; public double H => Y1 - Y0;
        public Box Inflate(double d) => new(X0 - d, Y0 - d, X1 + d, Y1 + d);
        public bool Overlaps(Box o) => X0 < o.X1 && o.X0 < X1 && Y0 < o.Y1 && o.Y0 < Y1;
    }


    /// <summary>The page as it is DISPLAYED (rotation applied, origin bottom left). Everything the tools say and take is in this space; annotations are written back in the page's own space.</summary>
    public sealed class Geo
    {
        public readonly double X0, Y0, W, H; public readonly int Rot; public readonly double Dw, Dh;
        /// <summary>Annotation scale: text, line weights and clouds follow the sheet (1 = A3; an A0 sheet gets about 2.8 times larger marks so they read as well on a screen).</summary>
        public double K => Math.Clamp(Math.Max(Dw, Dh) / 1191.0, 0.6, 4.0);
        public Geo(UglyToad.PdfPig.Content.Page page)
        {
            var mb = page.MediaBox.Bounds; X0 = Math.Min(mb.Left, mb.Right); Y0 = Math.Min(mb.Bottom, mb.Top); W = Math.Abs(mb.Right - mb.Left); H = Math.Abs(mb.Top - mb.Bottom);
            Rot = ((page.Rotation.Value % 360) + 360) % 360; (Dw, Dh) = Rot is 90 or 270 ? (H, W) : (W, H);
        }
        public (double X, double Y) ToDisp(double ux, double uy)
        {
            ux -= X0; uy -= Y0;
            return Rot switch { 90 => (uy, W - ux), 180 => (W - ux, H - uy), 270 => (H - uy, ux), _ => (ux, uy) };
        }
        public (double X, double Y) ToUser(double dx, double dy)
        {
            var (x, y) = Rot switch { 90 => (W - dy, dx), 180 => (W - dx, H - dy), 270 => (dy, H - dx), _ => (dx, dy) };
            return (x + X0, y + Y0);
        }
        /// <summary>PdfPig reports what is ON the page (text, lines, images) already in the displayed orientation: no rotation to apply, only the box to normalise.</summary>
        public Box DispBox(UglyToad.PdfPig.Core.PdfRectangle r)
        {
            var xs = new[] { r.BottomLeft.X, r.TopRight.X, r.TopLeft.X, r.BottomRight.X }; var ys = new[] { r.BottomLeft.Y, r.TopRight.Y, r.TopLeft.Y, r.BottomRight.Y };
            return new Box(xs.Min(), ys.Min(), xs.Max(), ys.Max());
        }
        public Box UserRect(Box d)
        {
            var pts = new[] { ToUser(d.X0, d.Y0), ToUser(d.X1, d.Y1), ToUser(d.X0, d.Y1), ToUser(d.X1, d.Y0) };
            return new Box(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y));
        }
        /// <summary>The Form /Matrix that takes the display-space drawing of an annotation into the page's own space.</summary>
        public double[] Matrix() => Rot switch
        {
            90 => new[] { 0.0, 1, -1, 0, X0 + W, Y0 },
            180 => new[] { -1.0, 0, 0, -1, X0 + W, Y0 + H },
            270 => new[] { 0.0, -1, 1, 0, X0, Y0 + H },
            _ => new[] { 1.0, 0, 0, 1, X0, Y0 },
        };
    }

    public sealed record Hit(int Page, string Text, Box Box);

    static readonly Dictionary<string, double[]> Colors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = new[] { 1.0, 0, 0 }, ["blue"] = new[] { 0.0, 0.2, 1.0 }, ["green"] = new[] { 0.0, 0.6, 0.1 }, ["yellow"] = new[] { 1.0, 1.0, 0 }, ["black"] = new[] { 0.0, 0, 0 },
    };

    // ------------------------------------------------------------------ finding text on a page

    /// <summary>
    /// Finds a text (a word, or several words) on the pages, any case, and returns where each occurrence is (PDF points of the page as displayed, origin bottom left).
    /// It reads the letters in the order they were written, so it works on rotated pages too. Word breaks are kept (a blank, a gap wider than about half a letter, a jump to another line): a search matches WHOLE words by default,
    /// so a short label such as "DP" is not found inside "APD PRD" or "AND PROVIDED"; "SET PRESSURE" is still found when the drawing has no blank letter between the words. <paramref name="contains"/> allows a match inside a word.
    /// </summary>
    public static List<Hit> Find(string path, string text, string? pages = null, bool contains = false)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("File not found: " + path);
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) throw new ArgumentException("Give the text to find.");
        var pattern = string.Join(" ?", tokens.Select(System.Text.RegularExpressions.Regex.Escape));
        if (!contains)
        {
            if (char.IsLetterOrDigit(tokens[0][0])) pattern = @"(?<![\p{L}\p{N}\-_])" + pattern;       // a hyphen is part of a tag: "512F" is not "PSV-512F"
            if (char.IsLetterOrDigit(tokens[^1][^1])) pattern += @"(?![\p{L}\p{N}\-_])";
        }
        var rx = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var hits = new List<Hit>();
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
        foreach (var n in PdfKit.ParsePages(pages, pdf.NumberOfPages))
        {
            var pg = pdf.GetPage(n); var geo = new Geo(pg);
            var all = pg.Letters.ToList();
            var letters = new List<(UglyToad.PdfPig.Content.Letter L, Box B)>();
            var hay = new StringBuilder(); var owner = new List<int>();            // owner: index into letters, -1 for a word break
            Box? prev = null; var prevSize = 0.0;
            foreach (var l in all)
            {
                if (string.IsNullOrWhiteSpace(l.Value)) { if (hay.Length > 0 && owner[^1] != -1) { hay.Append(' '); owner.Add(-1); } prev = null; continue; }
                var b = geo.DispBox(l.GlyphRectangle); var size = Math.Max(1, (double)l.PointSize);
                if (prev != null)
                {
                    var dx = Math.Max(0, Math.Max(prev.X0, b.X0) - Math.Min(prev.X1, b.X1)); var dy = Math.Max(0, Math.Max(prev.Y0, b.Y0) - Math.Min(prev.Y1, b.Y1));
                    var s = Math.Max(size, prevSize);
                    if (Math.Sqrt(dx * dx + dy * dy) > 0.55 * s && owner[^1] != -1) { hay.Append(' '); owner.Add(-1); }
                }
                letters.Add((l, b));
                foreach (var ch in l.Value) { hay.Append(ch); owner.Add(letters.Count - 1); }
                prev = b; prevSize = size;
            }
            var h = hay.ToString();
            foreach (System.Text.RegularExpressions.Match m in rx.Matches(h))
            {
                var firstChar = Enumerable.Range(m.Index, m.Length).First(i => owner[i] >= 0); var lastChar = Enumerable.Range(m.Index, m.Length).Last(i => owner[i] >= 0);
                var first = owner[firstChar]; var last = owner[lastChar];
                var boxes = Enumerable.Range(first, last - first + 1).Select(i => letters[i].B).ToList();
                var b = new Box(boxes.Min(x => x.X0), boxes.Min(x => x.Y0), boxes.Max(x => x.X1), boxes.Max(x => x.Y1));
                var lh = boxes.Max(x => Math.Min(x.W, x.H));
                if (Math.Min(b.W, b.H) > 3 * Math.Max(lh, 1) + 2) continue;                        // the letters are not on one line
                hits.Add(new Hit(n, string.Concat(Enumerable.Range(first, last - first + 1).Select(i => letters[i].L.Value)), b));
            }
        }
        return hits;
    }

    // ------------------------------------------------------------------ free space on a page

    sealed class Space
    {
        public readonly double Cell;
        public readonly double X0, Y0, W, H; public readonly int Nx, Ny; readonly bool[,] _busy;
        int[,]? _sum;
        public Space(Box page, double cell = 4) { Cell = cell; X0 = page.X0; Y0 = page.Y0; W = page.W; H = page.H; Nx = (int)Math.Ceiling(W / Cell); Ny = (int)Math.Ceiling(H / Cell); _busy = new bool[Nx, Ny]; }
        public void Block(Box b, double margin = 0)
        {
            var i0 = Math.Max(0, (int)Math.Floor((b.X0 - margin - X0) / Cell)); var i1 = Math.Min(Nx - 1, (int)Math.Floor((b.X1 + margin - X0) / Cell));
            var j0 = Math.Max(0, (int)Math.Floor((b.Y0 - margin - Y0) / Cell)); var j1 = Math.Min(Ny - 1, (int)Math.Floor((b.Y1 + margin - Y0) / Cell));
            for (var i = i0; i <= i1; i++) for (var j = j0; j <= j1; j++) _busy[i, j] = true;
            _sum = null;
        }
        public void BlockSegment(double ax, double ay, double bx, double by, double margin)
        {
            var len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)); var steps = Math.Max(1, (int)(len / (Cell / 2)));
            for (var s = 0; s <= steps; s++) { var t = (double)s / steps; var x = ax + (bx - ax) * t; var y = ay + (by - ay) * t; Block(new Box(x, y, x, y), margin); }
        }
        void Build()
        {
            _sum = new int[Nx + 1, Ny + 1];
            for (var i = 0; i < Nx; i++) for (var j = 0; j < Ny; j++) _sum[i + 1, j + 1] = _sum[i, j + 1] + _sum[i + 1, j] - _sum[i, j] + (_busy[i, j] ? 1 : 0);
        }
        public bool Free(Box b)
        {
            if (b.X0 < X0 + 2 || b.Y0 < Y0 + 2 || b.X1 > X0 + W - 2 || b.Y1 > Y0 + H - 2) return false;
            if (_sum == null) Build();
            var i0 = (int)Math.Floor((b.X0 - X0) / Cell); var i1 = Math.Min(Nx - 1, (int)Math.Floor((b.X1 - X0) / Cell));
            var j0 = (int)Math.Floor((b.Y0 - Y0) / Cell); var j1 = Math.Min(Ny - 1, (int)Math.Floor((b.Y1 - Y0) / Cell));
            return _sum![i1 + 1, j1 + 1] - _sum[i0, j1 + 1] - _sum[i1 + 1, j0] + _sum[i0, j0] == 0;
        }
        /// <summary>The free spot nearest to <paramref name="near"/> that fits w x h (null if there is none).</summary>
        public Box? Place(double w, double h, Box near, Func<Box, bool>? extra = null)
        {
            Box? best = null; var bestD = double.MaxValue;
            var cx = (near.X0 + near.X1) / 2; var cy = (near.Y0 + near.Y1) / 2;
            for (var x = X0 + 2; x + w <= X0 + W - 2; x += Cell)
                for (var y = Y0 + 2; y + h <= Y0 + H - 2; y += Cell)
                {
                    var b = new Box(x, y, x + w, y + h);
                    var dx = Math.Max(0, Math.Max(near.X0 - b.X1, b.X0 - near.X1)); var dy = Math.Max(0, Math.Max(near.Y0 - b.Y1, b.Y0 - near.Y1));
                    var d = dx * dx + dy * dy + 0.01 * ((x + w / 2 - cx) * (x + w / 2 - cx) + (y + h / 2 - cy) * (y + h / 2 - cy));
                    if (d >= bestD || near.Inflate(2).Overlaps(b) || !Free(b) || (extra != null && !extra(b))) continue;
                    best = b; bestD = d;
                }
            return best;
        }
    }

    static Space BuildSpace(UglyToad.PdfPig.Content.Page page, Geo g)
    {
        var sp = new Space(new Box(0, 0, g.Dw, g.Dh), 4 * g.K);
        foreach (var l in page.Letters) sp.Block(g.DispBox(l.GlyphRectangle), 1.5 * g.K);
        try
        {
            foreach (var path in page.Paths)
                foreach (var sub in path)
                {
                    PdfPoint? start = null; PdfPoint? first = null;
                    void Seg(PdfPoint a, PdfPoint b, double m) => sp.BlockSegment(a.X, a.Y, b.X, b.Y, m);
                    foreach (var c in sub.Commands)
                        switch (c)
                        {
                            case PdfSubpath.Move m: start = m.Location; first ??= m.Location; break;
                            case PdfSubpath.Line l: if (start is { } s) Seg(s, l.To, 1 * g.K); start = l.To; break;
                            case PdfSubpath.BezierCurve b: if (start is { } s2) Seg(s2, b.EndPoint, 2 * g.K); start = b.EndPoint; break;
                            case PdfSubpath.Close: if (start is { } s3 && first is { } f0) Seg(s3, f0, 1 * g.K); break;
                        }
                }
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or ArgumentException) { }       // linework unreadable: words only
        foreach (var im in page.GetImages()) sp.Block(g.DispBox(im.Bounds), 0);
        return sp;
    }

    // ------------------------------------------------------------------ drawing the annotations

    sealed class Item
    {
        public int Page = 1; public string Type = "cloud"; public Box? Rect; public string? Comment, Find, Color = "red";
        public int Occurrence; public bool All; public bool Contains; public double[]? At; public List<(string Text, string Color)> Lines = new();
        public double[]? Px, InRect; public double InDpi = 100;          // a box measured on a picture from pdf_render: pixels, the picture's rect (default: the whole displayed page) and dpi
    }

    /// <summary>Marker in the PDF keywords of a file made by this tool: such a file may be replaced by the next version of the same markup without asking again.</summary>
    public const string Marker = "PEC-QC-MARKUP";

    /// <summary>True when the PDF was written by pdf_markup (so regenerating it is not the replacement of someone's document).</summary>
    public static bool IsOwnMarkup(string path)
    {
        try { using var pdf = UglyToad.PdfPig.PdfDocument.Open(path); return (pdf.Information.Keywords ?? "").Contains(Marker, StringComparison.Ordinal); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or UglyToad.PdfPig.Core.PdfDocumentFormatException or InvalidOperationException) { return false; }
    }

    // ---- text for the drawing: capitals, and only what the Helvetica / WinAnsi font can show (degree, squared, cubed, plus-minus ... are fine; the rest is spelt out, never a '?')
    static readonly Dictionary<char, string> Spell = new()
    {
        ['‘'] = "'", ['’'] = "'", ['“'] = "\"", ['”'] = "\"", ['″'] = "\"", ['′'] = "'", ['–'] = "-", ['—'] = "-", ['−'] = "-", ['≤'] = "<=", ['≥'] = ">=", ['≠'] = "<>",
        ['Δ'] = "DELTA", ['Ω'] = "OHM", ['φ'] = "DIA", ['⌀'] = "DIA", ['→'] = "->", ['←'] = "<-", ['…'] = "...", [' '] = " ", ['€'] = "EUR", ['™'] = "TM", ['✓'] = "OK",
    };
    static string Normalize(string text, List<string> warnings)
    {
        var sb = new StringBuilder(); var lost = new HashSet<char>();
        foreach (var c0 in text.Trim())
        {
            if (Spell.TryGetValue(c0, out var s)) { sb.Append(s); continue; }
            var u = char.ToUpperInvariant(c0); if (u > 255) u = c0;                  // 'µ' stays 'µ' (its capital is Greek)
            if (u < 256 && (u >= 32 && u != 127 && (u < 127 || u >= 160))) sb.Append(u); else if (!char.IsControl(c0)) { sb.Append('?'); lost.Add(c0); }
        }
        if (lost.Count > 0) warnings.Add($"The comment '{sb}' has characters the drawing font cannot show ({string.Join(" ", lost.Select(c => "U+" + ((int)c).ToString("X4")))}): they show as '?'. Spell them out (for example DEGREE, M3).");
        return sb.ToString();
    }

    public sealed record Result(string Saved, int Annotations, int Clouds, int Highlights, int Comments, List<string> Warnings, List<Placed> Placed);
    public sealed record Placed(int Page, string? Comment, bool Legend, double[] Box, double[]? Cloud);

    /// <summary>Adds the markup items (JSON array) to the PDF and saves the result as a new file.</summary>
    public static Result Apply(string input, string output, string itemsJson, bool overwrite)
    {
        if (!File.Exists(input)) throw new FileNotFoundException("File not found: " + input);
        if (!output.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) output += ".pdf";
        if (Path.GetFullPath(input).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase)) throw new IOException("The markup is saved as a new file; the drawing itself is never changed. Choose another output name.");
        if (File.Exists(output) && !overwrite) throw new IOException($"{output} already exists: pass overwrite=true to replace it.");
        var items = Parse(itemsJson);
        var warnings = new List<string>(); var placed = new List<Placed>();

        using var plumb = UglyToad.PdfPig.PdfDocument.Open(input);
        var geos = new Dictionary<int, Geo>();
        Geo G(int pg) { if (!geos.TryGetValue(pg, out var g)) geos[pg] = g = new Geo(plumb.GetPage(pg)); return g; }

        // find the text the items name; turn boxes measured on a picture into page points
        var expanded = new List<Item>();
        foreach (var it in items)
        {
            if (it.Page < 1 || it.Page > plumb.NumberOfPages) { warnings.Add($"Page {it.Page} does not exist (the PDF has {plumb.NumberOfPages})."); continue; }
            if (it.Rect == null && it.Px != null)
            {
                var g = G(it.Page); var z = it.InRect ?? new[] { 0, 0, g.Dw, g.Dh }; var sc = it.InDpi / 72.0; var v = it.Px;
                var zx0 = Math.Min(z[0], z[2]); var zy1 = Math.Max(z[1], z[3]);
                it.Rect = new Box(zx0 + Math.Min(v[0], v[2]) / sc, zy1 - Math.Max(v[1], v[3]) / sc, zx0 + Math.Max(v[0], v[2]) / sc, zy1 - Math.Min(v[1], v[3]) / sc);
            }
            if (it.Rect != null || it.Type == "legend") { expanded.Add(it); continue; }
            if (string.IsNullOrWhiteSpace(it.Find)) throw new ArgumentException($"A '{it.Type}' item needs 'rect' [x0,y0,x1,y1] (PDF points, origin bottom left), 'px' (a box on a picture from pdf_render) or 'find' (the text to mark).");
            var hits = Find(input, it.Find!, it.Page.ToString(), it.Contains);
            if (hits.Count == 0) { warnings.Add($"Text '{it.Find}' was not found on page {it.Page} as a whole word: no {it.Type} placed (use pdf_find to see how it is written, 'contains':true for part of a word, or give 'rect')."); continue; }
            var chosen = it.All ? hits : it.Occurrence > 0 ? hits.Skip(it.Occurrence - 1).Take(1).ToList() : hits.Take(1).ToList();
            if (!it.All && it.Occurrence == 0 && hits.Count > 1) warnings.Add($"'{it.Find}' occurs {hits.Count} times on page {it.Page}: the first was marked (use 'all':true or 'occurrence').");
            foreach (var h in chosen) expanded.Add(new Item { Page = it.Page, Type = it.Type, Rect = h.Box, Comment = it.Comment, Color = it.Color, At = it.At });
        }

        var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        doc.Info.Keywords = string.IsNullOrWhiteSpace(doc.Info.Keywords) ? Marker : doc.Info.Keywords + "; " + Marker;
        var spaces = new Dictionary<int, Space>();
        PdfDictionary? font = null;
        int clouds = 0, hl = 0, notes = 0, total = 0;
        var cloudBoxes = new Dictionary<int, List<Box>>(); var commentBoxes = new Dictionary<int, List<Box>>(); var leaders = new Dictionary<int, List<(double X0, double Y0, double X1, double Y1)>>();
        List<T> Of<T>(Dictionary<int, List<T>> d, int pg) { if (!d.TryGetValue(pg, out var l)) d[pg] = l = new List<T>(); return l; }

        // clouds, highlights and strike-outs first, so every comment is placed knowing where all of them are
        foreach (var it in expanded.Where(x => x.Type != "legend"))
        {
            if (!spaces.ContainsKey(it.Page)) spaces[it.Page] = BuildSpace(plumb.GetPage(it.Page), G(it.Page));
            var page = doc.Pages[it.Page - 1];
            if (it.Type == "highlight") { AddHighlight(doc, page, G(it.Page), it.Rect!); hl++; total++; }
            else if (it.Type == "strikeout") { AddStrike(doc, page, G(it.Page), it.Rect!, it.Color!); hl++; total++; if (!string.IsNullOrWhiteSpace(it.Comment)) it.Type = "cloud-comment-only"; }
            else if (it.Type == "cloud") { AddCloud(doc, page, G(it.Page), it.Rect!.Inflate(3 * G(it.Page).K), it.Color!, it.Comment); clouds++; total++; spaces[it.Page].Block(it.Rect!.Inflate(5 * G(it.Page).K)); Of(cloudBoxes, it.Page).Add(it.Rect!.Inflate(3 * G(it.Page).K)); }
            else warnings.Add($"Unknown item type '{it.Type}' (cloud, highlight, strikeout or legend).");
        }
        font = Font(doc);
        // the legend before the comments: it keeps its place on a crowded sheet
        foreach (var it in expanded.Where(x => x.Type == "legend"))
        {
            if (!spaces.ContainsKey(it.Page)) spaces[it.Page] = BuildSpace(plumb.GetPage(it.Page), G(it.Page));
            var sp = spaces[it.Page]; var page = doc.Pages[it.Page - 1];
            var lk = G(it.Page).K; var wrapped = it.Lines.SelectMany(l => Wrap(Normalize(l.Text, warnings), 7 * lk, 230 * lk).Lines.Select(x => (x, l.Color))).ToList();
            var w = wrapped.Count == 0 ? 50 * lk : wrapped.Max(l => TextWidth(l.x, 7 * lk)) + 10 * lk; var h = wrapped.Count * 9 * lk + 8 * lk;
            var box = it.At is { Length: 2 } a ? new Box(a[0], a[1], a[0] + w, a[1] + h) : sp.Place(w, h, new Box(sp.X0 + sp.W - 5 * lk, sp.Y0 + 5 * lk, sp.X0 + sp.W - 5 * lk, sp.Y0 + 5 * lk));
            if (box == null) { warnings.Add("No free space for the legend on page " + it.Page + ": give 'at' [x,y] (its bottom left corner)."); continue; }
            sp.Block(box.Inflate(2 * lk));
            Of(commentBoxes, it.Page).Add(box);
            AddLegend(doc, page, G(it.Page), font, box, wrapped);
            total++; notes++;
            placed.Add(new Placed(it.Page, null, true, Arr(box), null));
        }
        foreach (var it in expanded.Where(x => (x.Type == "cloud" || x.Type == "cloud-comment-only") && !string.IsNullOrWhiteSpace(x.Comment)))
        {
            var sp = spaces[it.Page]; var page = doc.Pages[it.Page - 1];
            var text = Normalize(it.Comment!, warnings);
            var gk = G(it.Page); var (lines, w, h) = Wrap(text, 7 * gk.K, 150 * gk.K);
            var cloud = it.Rect!.Inflate(3 * gk.K);
            // the leader of a comment must not cross another comment, another cloud, or another leader (a comment far away is worse than none: the tool says so and the 'at' of the item places it)
            bool Clear(Box b) => LeaderClear(b, cloud, Of(commentBoxes, it.Page), Of(cloudBoxes, it.Page), Of(leaders, it.Page), 220 * gk.K);
            Box? box = it.At is { Length: 2 } a ? new Box(a[0], a[1], a[0] + w, a[1] + h) : sp.Place(w, h, cloud, Clear);
            if (box == null) { warnings.Add($"No place for the comment '{text}' close to its cloud without crossing another comment or leader: it was NOT written. Give 'at' [x,y] (the bottom left corner of its box) or shorten it."); continue; }
            sp.Block(box.Inflate(2 * gk.K));
            var (fx, fy, tx, ty) = Leader(box, cloud);
            sp.BlockSegment(fx, fy, tx, ty, 1 * gk.K);
            Of(commentBoxes, it.Page).Add(box); Of(leaders, it.Page).Add((fx, fy, tx, ty));
            AddComment(doc, page, G(it.Page), font, box, lines, it.Color!, text);
            AddArrow(doc, page, G(it.Page), fx, fy, tx, ty, it.Color!);
            notes++; total += 2;
            placed.Add(new Placed(it.Page, text, false, Arr(box), Arr(it.Rect!)));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        doc.Save(output);
        doc.Dispose();
        return new Result(output, total, clouds, hl, notes, warnings, placed);
    }

    static double[] Arr(Box b) => new[] { Math.Round(b.X0, 1), Math.Round(b.Y0, 1), Math.Round(b.X1, 1), Math.Round(b.Y1, 1) };

    static List<Item> Parse(string json)
    {
        using var d = JsonDocument.Parse(json);
        if (d.RootElement.ValueKind != JsonValueKind.Array) throw new ArgumentException("'items' must be a JSON array of {page, type, rect | find, comment, color}.");
        var list = new List<Item>();
        foreach (var e in d.RootElement.EnumerateArray())
        {
            var it = new Item();
            if (e.TryGetProperty("page", out var p) && p.TryGetInt32(out var pn)) it.Page = pn;
            if (e.TryGetProperty("type", out var t)) it.Type = (t.GetString() ?? "cloud").ToLowerInvariant();
            if (e.TryGetProperty("rect", out var r) && r.ValueKind == JsonValueKind.Array && r.GetArrayLength() == 4) { var v = r.EnumerateArray().Select(x => x.GetDouble()).ToArray(); it.Rect = new Box(Math.Min(v[0], v[2]), Math.Min(v[1], v[3]), Math.Max(v[0], v[2]), Math.Max(v[1], v[3])); }
            if (e.TryGetProperty("px", out var px) && px.ValueKind == JsonValueKind.Array && px.GetArrayLength() == 4)
            {
                // a box measured on a picture made by pdf_render (pixels, origin top left): "in" says which picture ({"rect":[...], "dpi":N}; with no rect the picture is the whole displayed page, and dpi defaults to 100)
                it.Px = px.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                if (e.TryGetProperty("in", out var inn) && inn.ValueKind == JsonValueKind.Object)
                {
                    if (inn.TryGetProperty("rect", out var zr) && zr.ValueKind == JsonValueKind.Array && zr.GetArrayLength() == 4) it.InRect = zr.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                    if (inn.TryGetProperty("dpi", out var dp) && dp.TryGetDouble(out var dv) && dv > 0) it.InDpi = dv;
                }
            }
            if (e.TryGetProperty("contains", out var ct) && ct.ValueKind == JsonValueKind.True) it.Contains = true;
            if (e.TryGetProperty("at", out var at) && at.ValueKind == JsonValueKind.Array && at.GetArrayLength() == 2) it.At = at.EnumerateArray().Select(x => x.GetDouble()).ToArray();
            if (e.TryGetProperty("comment", out var c)) it.Comment = c.GetString();
            if (e.TryGetProperty("find", out var f)) it.Find = f.GetString();
            if (e.TryGetProperty("color", out var col)) it.Color = col.GetString() ?? "red";
            if (e.TryGetProperty("occurrence", out var oc) && oc.TryGetInt32(out var on)) it.Occurrence = on;
            if (e.TryGetProperty("all", out var al) && al.ValueKind == JsonValueKind.True) it.All = true;
            if (e.TryGetProperty("lines", out var ls) && ls.ValueKind == JsonValueKind.Array)
                foreach (var l in ls.EnumerateArray()) it.Lines.Add((l.TryGetProperty("text", out var lt) ? lt.GetString() ?? "" : "", l.TryGetProperty("color", out var lc) ? lc.GetString() ?? "black" : "black"));
            if (!Colors.ContainsKey(it.Color ?? "")) throw new ArgumentException($"Colour '{it.Color}' is not known (red, blue, green, yellow, black).");
            list.Add(it);
        }
        return list;
    }

    // ------------------------------------------------------------------ text layout (Helvetica)

    static readonly Dictionary<char, int> Wd = BuildWidths();
    static Dictionary<char, int> BuildWidths()
    {
        var d = new Dictionary<char, int>();
        foreach (var (s, w) in new[] { ("A", 667), ("B", 667), ("C", 722), ("D", 722), ("E", 667), ("F", 611), ("G", 778), ("H", 722), ("I", 278), ("J", 500), ("K", 667), ("L", 556), ("M", 833), ("N", 722), ("O", 778), ("P", 667), ("Q", 778), ("R", 722), ("S", 667), ("T", 611), ("U", 722), ("V", 667), ("W", 944), ("X", 667), ("Y", 667), ("Z", 611), (" ", 278), (".", 278), (",", 278), (":", 278), (";", 278), ("-", 333), ("/", 278), ("\"", 355), ("'", 191), ("(", 333), (")", 333), ("&", 667), ("%", 889), ("=", 584), ("+", 584) })
            d[s[0]] = w;
        return d;
    }
    static double TextWidth(string t, double fs) => t.Sum(c => Wd.TryGetValue(c, out var w) ? w : char.IsDigit(c) ? 556 : 560) * fs / 1000.0;

    static (List<string> Lines, double W, double H) Wrap(string text, double fs, double maxW)
    {
        var lines = new List<string>(); var cur = "";
        foreach (var w in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var next = cur.Length == 0 ? w : cur + " " + w;
            if (TextWidth(next, fs) > maxW && cur.Length > 0) { lines.Add(cur); cur = w; } else cur = next;
        }
        if (cur.Length > 0) lines.Add(cur);
        var width = lines.Count == 0 ? 20 : lines.Max(l => TextWidth(l, fs)) + fs * 1.15;
        return (lines, Math.Ceiling(width), lines.Count * (fs * 1.29) + fs * 0.86);
    }

    public static (double Fx, double Fy, double Tx, double Ty) Leader(Box box, Box cloud)
    {
        var bx = (box.X0 + box.X1) / 2; var by = (box.Y0 + box.Y1) / 2; var cx = (cloud.X0 + cloud.X1) / 2; var cy = (cloud.Y0 + cloud.Y1) / 2;
        var fx = Math.Clamp(cx, box.X0, box.X1); var fy = Math.Clamp(cy, box.Y0, box.Y1);
        var tx = Math.Clamp(bx, cloud.X0, cloud.X1); var ty = Math.Clamp(by, cloud.Y0, cloud.Y1);
        return (fx, fy, tx, ty);
    }

    // ------------------------------------------------------------------ PDF objects

    static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    static string Rgb(double[] c, bool stroke) => $"{N(c[0])} {N(c[1])} {N(c[2])} {(stroke ? "RG" : "rg")}";
    static PdfArray Nums(PdfDocument doc, params double[] v) { var a = new PdfArray(doc); foreach (var x in v) a.Elements.Add(new PdfReal(x)); return a; }

    static PdfDictionary Font(PdfDocument doc)
    {
        var f = new PdfDictionary(doc);
        f.Elements["/Type"] = new PdfName("/Font"); f.Elements["/Subtype"] = new PdfName("/Type1"); f.Elements["/BaseFont"] = new PdfName("/Helvetica"); f.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");
        doc.Internals.AddObject(f);
        return f;
    }

    static PdfDictionary Form(PdfDocument doc, Geo g, Box bbox, string content, PdfDictionary? font = null, bool multiply = false)
    {
        var ap = new PdfDictionary(doc);
        ap.Elements["/Type"] = new PdfName("/XObject"); ap.Elements["/Subtype"] = new PdfName("/Form");
        ap.Elements["/BBox"] = Nums(doc, bbox.X0, bbox.Y0, bbox.X1, bbox.Y1);
        if (g.Rot != 0 || g.X0 != 0 || g.Y0 != 0) ap.Elements["/Matrix"] = Nums(doc, g.Matrix());
        var res = new PdfDictionary(doc);
        if (font != null) { var fd = new PdfDictionary(doc); fd.Elements["/F1"] = font.ReferenceNotNull; res.Elements["/Font"] = fd; }
        if (multiply) { var gs = new PdfDictionary(doc); var g1 = new PdfDictionary(doc); g1.Elements["/Type"] = new PdfName("/ExtGState"); g1.Elements["/BM"] = new PdfName("/Multiply"); gs.Elements["/GS1"] = g1; res.Elements["/ExtGState"] = gs; }
        ap.Elements["/Resources"] = res;
        ap.CreateStream(Encoding.Latin1.GetBytes(content));                                // the text uses octal escapes for the non-ASCII letters (Esc), the drawing operators are ASCII
        doc.Internals.AddObject(ap);
        return ap;
    }

    static PdfDictionary Annot(PdfDocument doc, PdfPage page, Geo g, string subtype, Box dispRect, double[] color, PdfDictionary ap, string? contents)
    {
        var a = new PdfDictionary(doc);
        a.Elements["/Type"] = new PdfName("/Annot"); a.Elements["/Subtype"] = new PdfName("/" + subtype);
        var rect = g.UserRect(dispRect);
        a.Elements["/Rect"] = Nums(doc, rect.X0, rect.Y0, rect.X1, rect.Y1);
        a.Elements["/F"] = new PdfInteger(4);                                            // print
        a.Elements["/C"] = Nums(doc, color);
        a.Elements["/T"] = new PdfString("PEC QC");
        a.Elements["/NM"] = new PdfString(Guid.NewGuid().ToString());
        a.Elements["/M"] = new PdfString("D:" + DateTime.Now.ToString("yyyyMMddHHmmss"));
        if (contents != null) a.Elements["/Contents"] = new PdfString(contents);
        var apd = new PdfDictionary(doc); apd.Elements["/N"] = ap.ReferenceNotNull; a.Elements["/AP"] = apd;
        doc.Internals.AddObject(a);
        var arr = page.Elements.GetArray("/Annots");
        if (arr == null) { arr = new PdfArray(doc); page.Elements["/Annots"] = arr; }
        arr.Elements.Add(a.ReferenceNotNull);
        return a;
    }

    /// <summary>A revision cloud: a polygon annotation with the cloud border effect, drawn with scallops.</summary>
    static void AddCloud(PdfDocument doc, PdfPage page, Geo g, Box r, string color, string? comment)
    {
        var col = Colors[color];
        var bb = r.Inflate(6 * g.K);
        var sb = new StringBuilder();
        sb.Append(Rgb(col, true)).Append($" {N(1.1 * g.K)} w 1 j 1 J\n");
        var arc = 5.0 * g.K;
        // clockwise starting bottom left; each scallop is a bulge outward (to the right of the direction of travel)
        var pts = new List<(double x, double y)> { (r.X0, r.Y0), (r.X0, r.Y1), (r.X1, r.Y1), (r.X1, r.Y0) };
        sb.Append($"{N(pts[0].x)} {N(pts[0].y)} m\n");
        for (var i = 0; i < 4; i++)
        {
            var (ax, ay) = pts[i]; var (bx, by) = pts[(i + 1) % 4];
            var len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)); var n = Math.Max(1, (int)Math.Round(len / (arc * 1.6)));
            var ux = (bx - ax) / len; var uy = (by - ay) / len; var nx = -uy; var ny = ux;          // outward normal (left of the direction of travel: the loop runs clockwise)
            var step = len / n; var bulge = Math.Min(step * 0.45, 3.5 * g.K);
            for (var k = 0; k < n; k++)
            {
                var sx = ax + ux * step * k; var sy = ay + uy * step * k; var ex = sx + ux * step; var ey = sy + uy * step;
                sb.Append($"{N(sx + nx * bulge * 1.33)} {N(sy + ny * bulge * 1.33)} {N(ex + nx * bulge * 1.33)} {N(ey + ny * bulge * 1.33)} {N(ex)} {N(ey)} c\n");
            }
        }
        sb.Append("S\n");
        var ap = Form(doc, g, bb, sb.ToString());
        var a = Annot(doc, page, g, "Polygon", bb, col, ap, comment);
        var vp = new[] { g.ToUser(r.X0, r.Y0), g.ToUser(r.X0, r.Y1), g.ToUser(r.X1, r.Y1), g.ToUser(r.X1, r.Y0) };
        a.Elements["/Vertices"] = Nums(doc, vp.SelectMany(q => new[] { q.X, q.Y }).ToArray());
        var be = new PdfDictionary(doc); be.Elements["/S"] = new PdfName("/C"); be.Elements["/I"] = new PdfReal(1); a.Elements["/BE"] = be;
        var bs = new PdfDictionary(doc); bs.Elements["/W"] = new PdfReal(1.1 * g.K); a.Elements["/BS"] = bs;
    }

    /// <summary>A yellow highlight over the item (multiply blend: the text underneath stays readable).</summary>
    static void AddHighlight(PdfDocument doc, PdfPage page, Geo g, Box r)
    {
        var col = Colors["yellow"];
        var sb = new StringBuilder("/GS1 gs\n").Append(Rgb(col, false)).Append($"\n{N(r.X0)} {N(r.Y0)} {N(r.W)} {N(r.H)} re f\n");
        var ap = Form(doc, g, r, sb.ToString(), null, true);
        var a = Annot(doc, page, g, "Highlight", r, col, ap, null);
        var q = new[] { g.ToUser(r.X0, r.Y1), g.ToUser(r.X1, r.Y1), g.ToUser(r.X0, r.Y0), g.ToUser(r.X1, r.Y0) };
        a.Elements["/QuadPoints"] = Nums(doc, q.SelectMany(z => new[] { z.X, z.Y }).ToArray());
    }

    /// <summary>A strike-out line through the item (a StrikeOut annotation: editable in Bluebeam / Acrobat; the item is "deleted", the comment beside it says what replaces it).</summary>
    static void AddStrike(PdfDocument doc, PdfPage page, Geo g, Box r, string color)
    {
        var col = Colors[color];
        var ym = (r.Y0 + r.Y1) / 2;
        var sb = new StringBuilder(); sb.Append(Rgb(col, true)).Append($" {N(1.3 * g.K)} w\n{N(r.X0)} {N(ym)} m {N(r.X1)} {N(ym)} l S\n");
        var ap = Form(doc, g, r.Inflate(2 * g.K), sb.ToString());
        var a = Annot(doc, page, g, "StrikeOut", r, col, ap, null);
        var q = new[] { g.ToUser(r.X0, r.Y1), g.ToUser(r.X1, r.Y1), g.ToUser(r.X0, r.Y0), g.ToUser(r.X1, r.Y0) };
        a.Elements["/QuadPoints"] = Nums(doc, q.SelectMany(z => new[] { z.X, z.Y }).ToArray());
    }

    /// <summary>The small boxed comment: a free-text annotation (editable in Bluebeam / Acrobat), white fill, coloured border and text.</summary>
    static void AddComment(PdfDocument doc, PdfPage page, Geo g, PdfDictionary font, Box b, List<string> lines, string color, string text)
    {
        var col = Colors[color];
        var sb = new StringBuilder();
        var fs = 7 * g.K;
        sb.Append("1 1 1 rg ").Append(Rgb(col, true)).Append($" {N(0.8 * g.K)} w {N(b.X0)} {N(b.Y0)} {N(b.W)} {N(b.H)} re B\nBT\n/F1 {N(fs)} Tf\n").Append(Rgb(col, false)).Append('\n');
        var y = b.Y1 - fs * 0.43 - fs;
        foreach (var l in lines) { sb.Append($"1 0 0 1 {N(b.X0 + fs * 0.57)} {N(y)} Tm ({Esc(l)}) Tj\n"); y -= fs * 1.29; }
        sb.Append("ET\n");
        var ap = Form(doc, g, b, sb.ToString(), font);
        var a = Annot(doc, page, g, "FreeText", b, col, ap, text);
        a.Elements["/DA"] = new PdfString($"{N(col[0])} {N(col[1])} {N(col[2])} rg /Helv {N(fs)} Tf");
        a.Elements["/IT"] = new PdfName("/FreeTextTypeWriter");
    }

    static void AddLegend(PdfDocument doc, PdfPage page, Geo g, PdfDictionary font, Box b, List<(string x, string Color)> lines)
    {
        var sb = new StringBuilder();
        var fs = 7 * g.K;
        sb.Append($"1 1 1 rg 0 0 0 RG {N(0.6 * g.K)} w ").Append($"{N(b.X0)} {N(b.Y0)} {N(b.W)} {N(b.H)} re B\n");
        // a yellow line is black text on a yellow band (yellow text on white cannot be read)
        var y = b.Y1 - 4 * g.K - fs;
        foreach (var (t, c) in lines) { if (c.Equals("yellow", StringComparison.OrdinalIgnoreCase)) sb.Append($"1 1 0 rg {N(b.X0 + 2 * g.K)} {N(y - 2 * g.K)} {N(b.W - 4 * g.K)} {N(fs + 3 * g.K)} re f\n"); y -= 9 * g.K; }
        sb.Append($"BT\n/F1 {N(fs)} Tf\n");
        y = b.Y1 - 4 * g.K - fs;
        foreach (var (t, c) in lines) { sb.Append(Rgb(c.Equals("yellow", StringComparison.OrdinalIgnoreCase) ? Colors["black"] : Colors[c], false)).Append($"\n1 0 0 1 {N(b.X0 + 5 * g.K)} {N(y)} Tm ({Esc(t)}) Tj\n"); y -= 9 * g.K; }
        sb.Append("ET\n");
        var ap = Form(doc, g, b, sb.ToString(), font);
        var a = Annot(doc, page, g, "FreeText", b, Colors["black"], ap, string.Join("\n", lines.Select(l => l.x)));
        a.Elements["/DA"] = new PdfString($"0 0 0 rg /Helv {N(fs)} Tf");
    }

    /// <summary>The leader: a line annotation with an arrow head at the cloud.</summary>
    static void AddArrow(PdfDocument doc, PdfPage page, Geo g, double fx, double fy, double tx, double ty, string color)
    {
        var col = Colors[color];
        var dx = tx - fx; var dy = ty - fy; var len = Math.Max(0.1, Math.Sqrt(dx * dx + dy * dy)); var ux = dx / len; var uy = dy / len;
        var h = 4.5 * g.K; var hx = tx - ux * h; var hy = ty - uy * h;
        var sb = new StringBuilder();
        sb.Append(Rgb(col, true)).Append(' ').Append(Rgb(col, false)).Append($" {N(0.8 * g.K)} w\n{N(fx)} {N(fy)} m {N(tx)} {N(ty)} l S\n");
        sb.Append($"{N(tx)} {N(ty)} m {N(hx - uy * h * 0.45)} {N(hy + ux * h * 0.45)} l {N(hx + uy * h * 0.45)} {N(hy - ux * h * 0.45)} l f\n");
        var r = new Box(Math.Min(fx, tx) - 6 * g.K, Math.Min(fy, ty) - 6 * g.K, Math.Max(fx, tx) + 6 * g.K, Math.Max(fy, ty) + 6 * g.K);
        var ap = Form(doc, g, r, sb.ToString());
        var a = Annot(doc, page, g, "Line", r, col, ap, null);
        var (ufx, ufy) = g.ToUser(fx, fy); var (utx, uty) = g.ToUser(tx, ty);
        a.Elements["/L"] = Nums(doc, ufx, ufy, utx, uty);
        var le = new PdfArray(doc); le.Elements.Add(new PdfName("/None")); le.Elements.Add(new PdfName("/ClosedArrow")); a.Elements["/LE"] = le;
    }

    /// <summary>A PDF string literal's content: brackets and backslash escaped, letters above ASCII as octal escapes (WinAnsi: the degree sign is \260, cubed \263), so the stream stays plain ASCII.</summary>
    static string Esc(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (c == '\\' || c == '(' || c == ')') sb.Append('\\').Append(c);
            else if (c < 32) continue;
            else if (c < 127) sb.Append(c);
            else if (c < 256) sb.Append('\\').Append(Convert.ToString(c, 8).PadLeft(3, '0'));
            else sb.Append('?');
        }
        return sb.ToString();
    }

    /// <summary>
    /// May a comment box be placed here? Its leader (from the box to its cloud) must be short and must not run through another comment box, another cloud, or cross another leader:
    /// a comment far away, or an arrow across the sheet, is worse than none (the caller then says so and the item's 'at' places it by hand).
    /// </summary>
    public static bool LeaderClear(Box box, Box cloud, IEnumerable<Box> comments, IEnumerable<Box> clouds, IEnumerable<(double X0, double Y0, double X1, double Y1)> leaders, double maxLength)
    {
        var (ax, ay, bx, by) = Leader(box, cloud);
        if (Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)) > maxLength) return false;
        foreach (var o in comments) if (SegmentHitsBox(ax, ay, bx, by, o)) return false;
        foreach (var o in clouds) if (!(o.X0 == cloud.X0 && o.Y0 == cloud.Y0 && o.X1 == cloud.X1 && o.Y1 == cloud.Y1) && SegmentHitsBox(ax, ay, bx, by, o)) return false;
        foreach (var l in leaders) if (SegmentsCross(ax, ay, bx, by, l.X0, l.Y0, l.X1, l.Y1)) return false;
        return true;
    }

    public static bool SegmentHitsBox(double ax, double ay, double bx, double by, Box b)
    {
        // Liang-Barsky clip of the segment against the box
        double t0 = 0, t1 = 1, dx = bx - ax, dy = by - ay;
        bool Clip(double p, double q) { if (Math.Abs(p) < 1e-9) return q >= 0; var r = q / p; if (p < 0) { if (r > t1) return false; if (r > t0) t0 = r; } else { if (r < t0) return false; if (r < t1) t1 = r; } return true; }
        return Clip(-dx, ax - b.X0) && Clip(dx, b.X1 - ax) && Clip(-dy, ay - b.Y0) && Clip(dy, b.Y1 - ay) && t0 <= t1;
    }

    public static bool SegmentsCross(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy)
    {
        double Cross(double ux, double uy, double vx, double vy) => ux * vy - uy * vx;
        var d1 = Cross(bx - ax, by - ay, cx - ax, cy - ay); var d2 = Cross(bx - ax, by - ay, dx - ax, dy - ay);
        var d3 = Cross(dx - cx, dy - cy, ax - cx, ay - cy); var d4 = Cross(dx - cx, dy - cy, bx - cx, by - cy);
        return d1 * d2 < 0 && d3 * d4 < 0;
    }
}
