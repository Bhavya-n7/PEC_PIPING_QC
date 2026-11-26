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

    public sealed record Hit(int Page, string Text, Box Box);

    static readonly Dictionary<string, double[]> Colors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = new[] { 1.0, 0, 0 }, ["blue"] = new[] { 0.0, 0.2, 1.0 }, ["green"] = new[] { 0.0, 0.6, 0.1 }, ["yellow"] = new[] { 1.0, 1.0, 0 }, ["black"] = new[] { 0.0, 0, 0 },
    };

    // ------------------------------------------------------------------ finding text on a page

    /// <summary>Finds a text (a word or several words in a row, any case) on the pages and returns where each occurrence is (PDF points, origin bottom left).</summary>
    public static List<Hit> Find(string path, string text, string? pages = null)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("File not found: " + path);
        var want = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (want.Length == 0) throw new ArgumentException("Give the text to find.");
        var hits = new List<Hit>();
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
        foreach (var n in PdfKit.ParsePages(pages, pdf.NumberOfPages))
        {
            var words = pdf.GetPage(n).GetWords().ToList();
            for (var i = 0; i < words.Count; i++)
            {
                if (want.Length == 1)
                {
                    if (words[i].Text.Contains(want[0], StringComparison.OrdinalIgnoreCase)) hits.Add(new Hit(n, words[i].Text, ToBox(words[i].BoundingBox)));
                    continue;
                }
                if (i + want.Length > words.Count) break;
                var ok = true;
                for (var k = 0; k < want.Length && ok; k++)
                    ok = k == 0 ? words[i].Text.EndsWith(want[0], StringComparison.OrdinalIgnoreCase) || words[i].Text.Equals(want[0], StringComparison.OrdinalIgnoreCase)
                        : k == want.Length - 1 ? words[i + k].Text.StartsWith(want[k], StringComparison.OrdinalIgnoreCase) : words[i + k].Text.Equals(want[k], StringComparison.OrdinalIgnoreCase);
                if (!ok) continue;
                var b = words[i].BoundingBox; var x0 = b.Left; var y0 = b.Bottom; var x1 = b.Right; var y1 = b.Top;
                for (var k = 1; k < want.Length; k++) { var c = words[i + k].BoundingBox; x0 = Math.Min(x0, c.Left); y0 = Math.Min(y0, c.Bottom); x1 = Math.Max(x1, c.Right); y1 = Math.Max(y1, c.Top); }
                if (y1 - y0 > 3 * (b.Top - b.Bottom) + 2) continue;      // not on one line
                hits.Add(new Hit(n, string.Join(' ', words.Skip(i).Take(want.Length).Select(w => w.Text)), new Box(x0, y0, x1, y1)));
            }
        }
        return hits;
    }

    static Box ToBox(UglyToad.PdfPig.Core.PdfRectangle r) => new(Math.Min(r.Left, r.Right), Math.Min(r.Bottom, r.Top), Math.Max(r.Left, r.Right), Math.Max(r.Bottom, r.Top));

    // ------------------------------------------------------------------ free space on a page

    sealed class Space
    {
        public const double Cell = 4;
        public readonly double X0, Y0, W, H; public readonly int Nx, Ny; readonly bool[,] _busy;
        int[,]? _sum;
        public Space(Box page) { X0 = page.X0; Y0 = page.Y0; W = page.W; H = page.H; Nx = (int)Math.Ceiling(W / Cell); Ny = (int)Math.Ceiling(H / Cell); _busy = new bool[Nx, Ny]; }
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

    static Space BuildSpace(UglyToad.PdfPig.Content.Page page)
    {
        var mb = page.MediaBox.Bounds;
        var sp = new Space(new Box(mb.Left, mb.Bottom, mb.Right, mb.Top));
        foreach (var w in page.GetWords()) sp.Block(ToBox(w.BoundingBox), 1.5);
        try
        {
            foreach (var path in page.Paths)
                foreach (var sub in path)
                {
                    PdfPoint? start = null;
                    foreach (var c in sub.Commands)
                        switch (c)
                        {
                            case PdfSubpath.Move m: start = m.Location; break;
                            case PdfSubpath.Line l: if (start is { } s) sp.BlockSegment(s.X, s.Y, l.To.X, l.To.Y, 1); start = l.To; break;
                            case PdfSubpath.BezierCurve b: if (start is { } s2) sp.BlockSegment(s2.X, s2.Y, b.EndPoint.X, b.EndPoint.Y, 2); start = b.EndPoint; break;
                            case PdfSubpath.Close: if (start is { } s3 && sub.Commands.FirstOrDefault() is PdfSubpath.Move m0) sp.BlockSegment(s3.X, s3.Y, m0.Location.X, m0.Location.Y, 1); break;
                        }
                }
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or ArgumentException) { }       // linework unreadable: words only
        foreach (var im in page.GetImages()) sp.Block(ToBox(im.Bounds), 0);
        return sp;
    }

    // ------------------------------------------------------------------ drawing the annotations

    sealed class Item
    {
        public int Page = 1; public string Type = "cloud"; public Box? Rect; public string? Comment, Find, Color = "red";
        public int Occurrence; public bool All; public double[]? At; public List<(string Text, string Color)> Lines = new();
        public bool KeepOut;
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

        // find the text the items name
        var expanded = new List<Item>();
        foreach (var it in items)
        {
            if (it.Rect != null || it.Type == "legend") { expanded.Add(it); continue; }
            if (string.IsNullOrWhiteSpace(it.Find)) throw new ArgumentException($"A '{it.Type}' item needs 'rect' [x0,y0,x1,y1] or 'find' (the text to mark).");
            var hits = Find(input, it.Find!, it.Page.ToString());
            if (hits.Count == 0) { warnings.Add($"Text '{it.Find}' was not found on page {it.Page}: no {it.Type} placed (give 'rect' instead)."); continue; }
            var chosen = it.All ? hits : it.Occurrence > 0 ? hits.Skip(it.Occurrence - 1).Take(1).ToList() : hits.Take(1).ToList();
            if (!it.All && it.Occurrence == 0 && hits.Count > 1) warnings.Add($"'{it.Find}' occurs {hits.Count} times on page {it.Page}: the first was marked (use 'all':true or 'occurrence').");
            foreach (var h in chosen) expanded.Add(new Item { Page = it.Page, Type = it.Type, Rect = h.Box, Comment = it.Comment, Color = it.Color, At = it.At });
        }

        using var plumb = UglyToad.PdfPig.PdfDocument.Open(input);
        var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        var spaces = new Dictionary<int, Space>();
        PdfDictionary? font = null;
        int clouds = 0, hl = 0, notes = 0, total = 0;

        // clouds and highlights first, so every comment is placed knowing where all of them are
        foreach (var it in expanded.Where(x => x.Type != "legend"))
        {
            if (it.Page < 1 || it.Page > doc.PageCount) { warnings.Add($"Page {it.Page} does not exist (the PDF has {doc.PageCount})."); continue; }
            if (!spaces.ContainsKey(it.Page)) spaces[it.Page] = BuildSpace(plumb.GetPage(it.Page));
            var page = doc.Pages[it.Page - 1];
            if (it.Type == "highlight") { AddHighlight(doc, page, it.Rect!); hl++; total++; }
            else if (it.Type == "cloud") { AddCloud(doc, page, it.Rect!.Inflate(3), it.Color!, it.Comment); clouds++; total++; spaces[it.Page].Block(it.Rect!.Inflate(5)); }
            else warnings.Add($"Unknown item type '{it.Type}' (cloud, highlight or legend).");
        }
        font = Font(doc);
        foreach (var it in expanded.Where(x => x.Type == "cloud" && !string.IsNullOrWhiteSpace(x.Comment)))
        {
            if (it.Page < 1 || it.Page > doc.PageCount) continue;
            var sp = spaces[it.Page]; var page = doc.Pages[it.Page - 1];
            var text = it.Comment!.Trim().ToUpperInvariant();
            var (lines, w, h) = Wrap(text, 7, 150);
            Box? box = it.At is { Length: 2 } a ? new Box(a[0], a[1], a[0] + w, a[1] + h) : sp.Place(w, h, it.Rect!.Inflate(3));
            if (box == null) { warnings.Add($"No free space for the comment '{text}' near its cloud: it was NOT written. Give 'at' [x,y] or shorten it."); continue; }
            sp.Block(box.Inflate(2));
            var (fx, fy, tx, ty) = Leader(box, it.Rect!.Inflate(3));
            sp.BlockSegment(fx, fy, tx, ty, 1);
            AddComment(doc, page, font, box, lines, it.Color!, text);
            AddArrow(doc, page, fx, fy, tx, ty, it.Color!);
            notes++; total += 2;
            placed.Add(new Placed(it.Page, text, false, Arr(box), Arr(it.Rect!)));
        }
        foreach (var it in expanded.Where(x => x.Type == "legend"))
        {
            if (it.Page < 1 || it.Page > doc.PageCount) { warnings.Add($"Page {it.Page} does not exist."); continue; }
            if (!spaces.ContainsKey(it.Page)) spaces[it.Page] = BuildSpace(plumb.GetPage(it.Page));
            var sp = spaces[it.Page]; var page = doc.Pages[it.Page - 1];
            var wrapped = it.Lines.SelectMany(l => Wrap(l.Text.ToUpperInvariant(), 7, 230).Lines.Select(x => (x, l.Color))).ToList();
            var w = wrapped.Count == 0 ? 50 : wrapped.Max(l => TextWidth(l.x, 7)) + 10; var h = wrapped.Count * 9 + 8;
            var box = it.At is { Length: 2 } a ? new Box(a[0], a[1], a[0] + w, a[1] + h) : sp.Place(w, h, new Box(sp.X0 + sp.W - 5, sp.Y0 + 5, sp.X0 + sp.W - 5, sp.Y0 + 5));
            if (box == null) { warnings.Add("No free space for the legend on page " + it.Page + ": give 'at' [x,y]."); continue; }
            sp.Block(box.Inflate(2));
            AddLegend(doc, page, font, box, wrapped);
            total++; notes++;
            placed.Add(new Placed(it.Page, null, true, Arr(box), null));
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
        var width = lines.Count == 0 ? 20 : lines.Max(l => TextWidth(l, fs)) + 8;
        return (lines, Math.Ceiling(width), lines.Count * (fs + 2) + 6);
    }

    static (double Fx, double Fy, double Tx, double Ty) Leader(Box box, Box cloud)
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

    static PdfDictionary Form(PdfDocument doc, Box bbox, string content, PdfDictionary? font = null, bool multiply = false)
    {
        var ap = new PdfDictionary(doc);
        ap.Elements["/Type"] = new PdfName("/XObject"); ap.Elements["/Subtype"] = new PdfName("/Form");
        ap.Elements["/BBox"] = Nums(doc, bbox.X0, bbox.Y0, bbox.X1, bbox.Y1);
        var res = new PdfDictionary(doc);
        if (font != null) { var fd = new PdfDictionary(doc); fd.Elements["/F1"] = font.ReferenceNotNull; res.Elements["/Font"] = fd; }
        if (multiply) { var gs = new PdfDictionary(doc); var g1 = new PdfDictionary(doc); g1.Elements["/Type"] = new PdfName("/ExtGState"); g1.Elements["/BM"] = new PdfName("/Multiply"); gs.Elements["/GS1"] = g1; res.Elements["/ExtGState"] = gs; }
        ap.Elements["/Resources"] = res;
        ap.CreateStream(Encoding.ASCII.GetBytes(content));
        doc.Internals.AddObject(ap);
        return ap;
    }

    static PdfDictionary Annot(PdfDocument doc, PdfPage page, string subtype, Box rect, double[] color, PdfDictionary ap, string? contents)
    {
        var a = new PdfDictionary(doc);
        a.Elements["/Type"] = new PdfName("/Annot"); a.Elements["/Subtype"] = new PdfName("/" + subtype);
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
    static void AddCloud(PdfDocument doc, PdfPage page, Box r, string color, string? comment)
    {
        var col = Colors[color];
        var bb = r.Inflate(6);
        var sb = new StringBuilder();
        sb.Append(Rgb(col, true)).Append(" 1.1 w 1 j 1 J\n");
        var arc = 5.0;
        // clockwise starting bottom left; each scallop is a bulge outward (to the right of the direction of travel)
        var pts = new List<(double x, double y)> { (r.X0, r.Y0), (r.X0, r.Y1), (r.X1, r.Y1), (r.X1, r.Y0) };
        sb.Append($"{N(pts[0].x)} {N(pts[0].y)} m\n");
        for (var i = 0; i < 4; i++)
        {
            var (ax, ay) = pts[i]; var (bx, by) = pts[(i + 1) % 4];
            var len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)); var n = Math.Max(1, (int)Math.Round(len / (arc * 1.6)));
            var ux = (bx - ax) / len; var uy = (by - ay) / len; var nx = -uy; var ny = ux;          // outward normal (left of the direction of travel: the loop runs clockwise)
            var step = len / n; var bulge = Math.Min(step * 0.45, 3.5);
            for (var k = 0; k < n; k++)
            {
                var sx = ax + ux * step * k; var sy = ay + uy * step * k; var ex = sx + ux * step; var ey = sy + uy * step;
                sb.Append($"{N(sx + nx * bulge * 1.33)} {N(sy + ny * bulge * 1.33)} {N(ex + nx * bulge * 1.33)} {N(ey + ny * bulge * 1.33)} {N(ex)} {N(ey)} c\n");
            }
        }
        sb.Append("S\n");
        var ap = Form(doc, bb, sb.ToString());
        var a = Annot(doc, page, "Polygon", bb, col, ap, comment);
        var verts = Nums(doc, r.X0, r.Y0, r.X0, r.Y1, r.X1, r.Y1, r.X1, r.Y0); a.Elements["/Vertices"] = verts;
        var be = new PdfDictionary(doc); be.Elements["/S"] = new PdfName("/C"); be.Elements["/I"] = new PdfReal(1); a.Elements["/BE"] = be;
        var bs = new PdfDictionary(doc); bs.Elements["/W"] = new PdfReal(1.1); a.Elements["/BS"] = bs;
    }

    /// <summary>A yellow highlight over the item (multiply blend: the text underneath stays readable).</summary>
    static void AddHighlight(PdfDocument doc, PdfPage page, Box r)
    {
        var col = Colors["yellow"];
        var sb = new StringBuilder("/GS1 gs\n").Append(Rgb(col, false)).Append($"\n{N(r.X0)} {N(r.Y0)} {N(r.W)} {N(r.H)} re f\n");
        var ap = Form(doc, r, sb.ToString(), null, true);
        var a = Annot(doc, page, "Highlight", r, col, ap, null);
        a.Elements["/QuadPoints"] = Nums(doc, r.X0, r.Y1, r.X1, r.Y1, r.X0, r.Y0, r.X1, r.Y0);
    }

    /// <summary>The small boxed comment: a free-text annotation (editable in Bluebeam / Acrobat), white fill, coloured border and text.</summary>
    static void AddComment(PdfDocument doc, PdfPage page, PdfDictionary font, Box b, List<string> lines, string color, string text)
    {
        var col = Colors[color];
        var sb = new StringBuilder();
        sb.Append("1 1 1 rg ").Append(Rgb(col, true)).Append($" 0.8 w {N(b.X0)} {N(b.Y0)} {N(b.W)} {N(b.H)} re B\nBT\n/F1 7 Tf\n").Append(Rgb(col, false)).Append('\n');
        var y = b.Y1 - 3 - 7;
        foreach (var l in lines) { sb.Append($"1 0 0 1 {N(b.X0 + 4)} {N(y)} Tm ({Esc(l)}) Tj\n"); y -= 9; }
        sb.Append("ET\n");
        var ap = Form(doc, b, sb.ToString(), font);
        var a = Annot(doc, page, "FreeText", b, col, ap, text);
        a.Elements["/DA"] = new PdfString($"{N(col[0])} {N(col[1])} {N(col[2])} rg /Helv 7 Tf");
        a.Elements["/IT"] = new PdfName("/FreeTextTypeWriter");
    }

    static void AddLegend(PdfDocument doc, PdfPage page, PdfDictionary font, Box b, List<(string x, string Color)> lines)
    {
        var sb = new StringBuilder();
        sb.Append("1 1 1 rg 0 0 0 RG 0.6 w ").Append($"{N(b.X0)} {N(b.Y0)} {N(b.W)} {N(b.H)} re B\nBT\n/F1 7 Tf\n");
        var y = b.Y1 - 4 - 7;
        foreach (var (t, c) in lines) { sb.Append(Rgb(Colors[c], false)).Append($"\n1 0 0 1 {N(b.X0 + 5)} {N(y)} Tm ({Esc(t)}) Tj\n"); y -= 9; }
        sb.Append("ET\n");
        var ap = Form(doc, b, sb.ToString(), font);
        var a = Annot(doc, page, "FreeText", b, Colors["black"], ap, string.Join("\n", lines.Select(l => l.x)));
        a.Elements["/DA"] = new PdfString("0 0 0 rg /Helv 7 Tf");
    }

    /// <summary>The leader: a line annotation with an arrow head at the cloud.</summary>
    static void AddArrow(PdfDocument doc, PdfPage page, double fx, double fy, double tx, double ty, string color)
    {
        var col = Colors[color];
        var dx = tx - fx; var dy = ty - fy; var len = Math.Max(0.1, Math.Sqrt(dx * dx + dy * dy)); var ux = dx / len; var uy = dy / len;
        var h = 4.5; var hx = tx - ux * h; var hy = ty - uy * h;
        var sb = new StringBuilder();
        sb.Append(Rgb(col, true)).Append(' ').Append(Rgb(col, false)).Append($" 0.8 w\n{N(fx)} {N(fy)} m {N(tx)} {N(ty)} l S\n");
        sb.Append($"{N(tx)} {N(ty)} m {N(hx - uy * h * 0.45)} {N(hy + ux * h * 0.45)} l {N(hx + uy * h * 0.45)} {N(hy - ux * h * 0.45)} l f\n");
        var r = new Box(Math.Min(fx, tx) - 6, Math.Min(fy, ty) - 6, Math.Max(fx, tx) + 6, Math.Max(fy, ty) + 6);
        var ap = Form(doc, r, sb.ToString());
        var a = Annot(doc, page, "Line", r, col, ap, null);
        a.Elements["/L"] = Nums(doc, fx, fy, tx, ty);
        var le = new PdfArray(doc); le.Elements.Add(new PdfName("/None")); le.Elements.Add(new PdfName("/ClosedArrow")); a.Elements["/LE"] = le;
    }

    static string Esc(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}

/// <summary>Draws a page, or a part of it, as a PNG so it can be looked at (zoom into a zone at high resolution).</summary>
public static class PdfRender
{
    public static (string Saved, int Width, int Height) Render(string path, int page, double[]? rect, int dpi, string output, bool overwrite)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("File not found: " + path);
        if (!output.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) output += ".png";
        if (File.Exists(output) && !overwrite) throw new IOException($"{output} already exists: pass overwrite=true.");
        dpi = Math.Clamp(dpi, 30, 600);
        var bytes = File.ReadAllBytes(path);
        using var pig = UglyToad.PdfPig.PdfDocument.Open(bytes);
        if (page < 1 || page > pig.NumberOfPages) throw new ArgumentException($"Page {page} does not exist (the PDF has {pig.NumberOfPages}).");
        var mb = pig.GetPage(page).MediaBox.Bounds;
        var scale = dpi / 72.0;
        var opt = new PDFtoImage.RenderOptions { Dpi = dpi, WithAnnotations = true, WithFormFill = false };
        using var bmp = PDFtoImage.Conversion.ToImage(bytes, page - 1, options: opt);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        if (rect is { Length: 4 })
        {
            // PDF points, origin bottom left -> pixels, origin top left
            var x0 = (int)Math.Floor((Math.Min(rect[0], rect[2]) - mb.Left) * scale); var x1 = (int)Math.Ceiling((Math.Max(rect[0], rect[2]) - mb.Left) * scale);
            var y0 = (int)Math.Floor((mb.Top - Math.Max(rect[1], rect[3])) * scale); var y1 = (int)Math.Ceiling((mb.Top - Math.Min(rect[1], rect[3])) * scale);
            x0 = Math.Clamp(x0, 0, bmp.Width - 1); y0 = Math.Clamp(y0, 0, bmp.Height - 1); x1 = Math.Clamp(x1, x0 + 1, bmp.Width); y1 = Math.Clamp(y1, y0 + 1, bmp.Height);
            using var crop = new SkiaSharp.SKBitmap(x1 - x0, y1 - y0);
            bmp.ExtractSubset(crop, new SkiaSharp.SKRectI(x0, y0, x1, y1));
            using var img = SkiaSharp.SKImage.FromBitmap(crop); using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(output, data.ToArray()); return (output, crop.Width, crop.Height);
        }
        using var full = SkiaSharp.SKImage.FromBitmap(bmp); using var d2 = full.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(output, d2.ToArray()); return (output, bmp.Width, bmp.Height);
    }
}
