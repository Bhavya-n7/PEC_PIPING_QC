using System.Globalization;
using SkiaSharp;

namespace Pec.Qc.Core.OfficeKit;

/// <summary>
/// Looking at a drawing the way a checker does: by the sheet's own grid zones, with the same zones on the client's drawing and on ours, at a zoom that follows the sheet size.
/// The zone grid is read from the tick marks of the sheet's border (columns along the top and bottom, rows along the sides); when a sheet has none, the ISO 5457 grid of its size class is used.
/// All rectangles are in PDF points of the page AS DISPLAYED (rotation applied, origin bottom left), the space pdf_find, pdf_render and pdf_markup share.
/// </summary>
public static class PdfZones
{
    public sealed record Tile(string Id, int Row, int Col, double[] Rect, int Dpi, string Cells);
    public sealed record Plan(int Page, double WidthMm, double HeightMm, string SheetClass, int Rotation, bool TextLayer, int Columns, int Rows, string GridSource, double[] ColumnEdges, double[] RowEdgesTopDown,
        int TilesAcross, int TilesDown, List<Tile> Tiles, List<string> Notes);

    // ------------------------------------------------------------------ rendering

    public static (string Saved, int Width, int Height) Render(string path, int page, double[]? rect, int dpi, string output, bool overwrite)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("File not found: " + path);
        if (!output.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) output += ".png";
        if (File.Exists(output) && !overwrite) throw new IOException($"{output} already exists: pass overwrite=true.");
        dpi = Math.Clamp(dpi, 30, 600);
        var bytes = File.ReadAllBytes(path);
        using var pig = UglyToad.PdfPig.PdfDocument.Open(bytes);
        if (page < 1 || page > pig.NumberOfPages) throw new ArgumentException($"Page {page} does not exist (the PDF has {pig.NumberOfPages}).");
        var g = new PdfMarkup.Geo(pig.GetPage(page));
        var scale = dpi / 72.0;
        if (rect is not { Length: 4 } && (long)(g.Dw * scale) * (long)(g.Dh * scale) > 120_000_000) throw new ArgumentException($"The whole page at {dpi} dpi is too large ({(int)(g.Dw * scale)} x {(int)(g.Dh * scale)} px): use a lower dpi, or a zone (rect).");
        using var bmp = PDFtoImage.Conversion.ToImage(bytes, page - 1, options: new PDFtoImage.RenderOptions { Dpi = dpi, WithAnnotations = true, WithFormFill = false });
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        if (rect is { Length: 4 })
        {
            var x0 = (int)Math.Floor(Math.Min(rect[0], rect[2]) * bmp.Width / g.Dw); var x1 = (int)Math.Ceiling(Math.Max(rect[0], rect[2]) * bmp.Width / g.Dw);
            var y0 = (int)Math.Floor((g.Dh - Math.Max(rect[1], rect[3])) * bmp.Height / g.Dh); var y1 = (int)Math.Ceiling((g.Dh - Math.Min(rect[1], rect[3])) * bmp.Height / g.Dh);
            x0 = Math.Clamp(x0, 0, bmp.Width - 1); y0 = Math.Clamp(y0, 0, bmp.Height - 1); x1 = Math.Clamp(x1, x0 + 1, bmp.Width); y1 = Math.Clamp(y1, y0 + 1, bmp.Height);
            using var crop = new SKBitmap(x1 - x0, y1 - y0);
            bmp.ExtractSubset(crop, new SKRectI(x0, y0, x1, y1));
            using var img = SKImage.FromBitmap(crop); using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(output, data.ToArray()); return (output, crop.Width, crop.Height);
        }
        using var full = SKImage.FromBitmap(bmp); using var d2 = full.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(output, d2.ToArray()); return (output, bmp.Width, bmp.Height);
    }

    // ------------------------------------------------------------------ sheet size

    public static string SheetClass(double widthMm, double heightMm)
    {
        var l = Math.Max(widthMm, heightMm); var s = Math.Min(widthMm, heightMm);
        (string N, double L, double S)[] std = { ("A0", 1189, 841), ("A1", 841, 594), ("A2", 594, 420), ("A3", 420, 297), ("A4", 297, 210) };
        foreach (var (n, L, S) in std) if (Math.Abs(l - L) <= 0.05 * L && Math.Abs(s - S) <= 0.06 * S) return n;
        var area = l * s; return std.OrderBy(x => Math.Abs(Math.Log(x.L * x.S / area))).First().N + "-like";      // an odd size (a plot with a margin): the nearest class by area
    }

    static (int Across, int Down) TileCounts(string cls, bool landscape)
    {
        var c = cls.Replace("-like", "");
        var longSide = c switch { "A0" => 4, "A1" => 4, "A2" => 3, "A3" => 3, _ => 2 };
        var shortSide = c switch { "A0" => 3, "A1" => 3, "A2" => 2, "A3" => 2, _ => 2 };
        return landscape ? (longSide, shortSide) : (shortSide, longSide);
    }

    static (int Cols, int Rows) IsoGrid(string cls, bool landscape)
    {
        var c = cls.Replace("-like", "");
        var (l, s) = c switch { "A0" => (24, 16), "A1" => (16, 12), "A2" => (12, 8), "A3" => (8, 6), _ => (6, 4) };
        return landscape ? (l, s) : (s, l);
    }

    // ------------------------------------------------------------------ the plan

    public static Plan MakePlan(string path, int page, string? colLabels = null, string? rowLabels = null, int across = 0, int down = 0)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("File not found: " + path);
        var bytes = File.ReadAllBytes(path);
        using var pig = UglyToad.PdfPig.PdfDocument.Open(bytes);
        if (page < 1 || page > pig.NumberOfPages) throw new ArgumentException($"Page {page} does not exist (the PDF has {pig.NumberOfPages}).");
        var pg = pig.GetPage(page); var g = new PdfMarkup.Geo(pg);
        var wmm = g.Dw / 72 * 25.4; var hmm = g.Dh / 72 * 25.4; var cls = SheetClass(wmm, hmm); var landscape = g.Dw >= g.Dh;
        var notes = new List<string>();
        var images = pg.GetImages().ToList();
        var words = pg.GetWords().Count();
        var textLayer = words >= 20;
        double nativeDpi = 0;
        if (images.Count > 0) { var big = images.OrderByDescending(i => i.Bounds.Width * i.Bounds.Height).First(); if (big.Bounds.Width > 1) nativeDpi = big.WidthInSamples / (big.Bounds.Width / 72.0); }
        if (words == 0) notes.Add("The page has no text layer (a scan, or text drawn as lines): pdf_find cannot find text here. Locate items on the zone pictures and give them to pdf_markup as pixel boxes (\"in\" + \"px\").");

        int cols = 0, rows = 0; double[] colEdges = Array.Empty<double>(), rowEdges = Array.Empty<double>(); var source = "";
        try
        {
            var dpi = (int)Math.Clamp(3000.0 / (Math.Max(g.Dw, g.Dh) / 72.0), 20, 300);
            using var bmp = PDFtoImage.Conversion.ToImage(bytes, page - 1, options: new PDFtoImage.RenderOptions { Dpi = dpi, WithAnnotations = false });
            var (c, r, cEdges, rEdges) = DetectGrid(bmp);
            if (c >= 4 && r >= 3) { cols = c; rows = r; colEdges = cEdges.Select(x => x / bmp.Width * g.Dw).ToArray(); rowEdges = rEdges.Select(y => y / bmp.Height * g.Dh).ToArray(); source = "detected from the border tick marks"; }
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or NotSupportedException or IOException) { notes.Add("Grid detection failed (" + e.GetType().Name + ")."); }
        if (cols == 0)
        {
            (cols, rows) = IsoGrid(cls, landscape); source = "ISO 5457 default for " + cls + " (no border tick marks found): check it against the sheet";
            colEdges = Enumerable.Range(0, cols + 1).Select(i => g.Dw * (0.03 + 0.94 * i / cols)).ToArray();
            rowEdges = Enumerable.Range(0, rows + 1).Select(i => g.Dh * (0.03 + 0.94 * i / rows)).ToArray();
            notes.Add("The default grid is approximate: use the zones only as a guide and read the sheet's own labels.");
        }
        var (ta, td) = TileCounts(cls, landscape);
        if (across <= 0) across = ta; if (down <= 0) down = td;
        across = Math.Clamp(across, 1, cols); down = Math.Clamp(down, 1, rows);
        var cl = Labels(colLabels, cols); var rl = Labels(rowLabels, rows);
        var tiles = new List<Tile>();
        for (var tr = 0; tr < down; tr++)
            for (var tc = 0; tc < across; tc++)
            {
                // the tile's grid cells (rows counted from the top), with one cell of overlap into the neighbours so nothing on a tile edge is cut
                var c0 = Math.Max(0, (int)Math.Floor((double)tc * cols / across) - (tc > 0 ? 1 : 0)); var c1 = Math.Min(cols - 1, (int)Math.Ceiling((double)(tc + 1) * cols / across) - 1 + (tc < across - 1 ? 1 : 0));
                var r0 = Math.Max(0, (int)Math.Floor((double)tr * rows / down) - (tr > 0 ? 1 : 0)); var r1 = Math.Min(rows - 1, (int)Math.Ceiling((double)(tr + 1) * rows / down) - 1 + (tr < down - 1 ? 1 : 0));
                var x0 = c0 == 0 ? 0 : colEdges[c0]; var x1 = c1 == cols - 1 ? g.Dw : colEdges[c1 + 1];
                var yTop = r0 == 0 ? g.Dh : g.Dh - rowEdges[r0]; var yBot = r1 == rows - 1 ? 0 : g.Dh - rowEdges[r1 + 1];
                var tileLongPt = Math.Max(x1 - x0, yTop - yBot);
                var dpiT = (int)Math.Clamp(1568.0 / (tileLongPt / 72.0), 40, 300);
                if (nativeDpi > 0) dpiT = (int)Math.Min(dpiT, Math.Max(100, nativeDpi * 1.2));          // no point rendering a scan above its own resolution
                tiles.Add(new Tile($"T{tr + 1}.{tc + 1}", tr + 1, tc + 1, new[] { Math.Round(x0, 1), Math.Round(yBot, 1), Math.Round(x1, 1), Math.Round(yTop, 1) }, dpiT, $"columns {cl[c0]}-{cl[c1]}, rows {rl[r0]}-{rl[r1]}"));
            }
        notes.Add($"Zone labels: columns are listed left to right ({cl[0]} ... {cl[^1]}), rows top to bottom ({rl[0]} ... {rl[^1]}). Look at the sheet border and pass the real labels (cols / rows, for example \"17-1\" and \"K-A\") if these are only numbers.");
        notes.Add("Use the SAME tiles on the client's drawing and on ours, also when the sheet sizes differ (an A3 scan and an A0 redraw): compare tile with tile.");
        return new Plan(page, Math.Round(wmm), Math.Round(hmm), cls, g.Rot, textLayer, cols, rows, source, colEdges.Select(x => Math.Round(x, 1)).ToArray(), rowEdges.Select(x => Math.Round(x, 1)).ToArray(), across, down, tiles, notes);
    }

    static string[] Labels(string? spec, int n)
    {
        var res = Enumerable.Range(1, n).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray();
        if (string.IsNullOrWhiteSpace(spec)) return res;
        var parts = spec.Split('-', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return res;
        if (int.TryParse(parts[0], out var a) && int.TryParse(parts[1], out var b)) { var step = b >= a ? 1 : -1; return Enumerable.Range(0, n).Select(i => (a + step * i).ToString(CultureInfo.InvariantCulture)).ToArray(); }
        if (parts[0].Length == 1 && parts[1].Length == 1) { var a1 = char.ToUpperInvariant(parts[0][0]); var b1 = char.ToUpperInvariant(parts[1][0]); var step = b1 >= a1 ? 1 : -1; return Enumerable.Range(0, n).Select(i => ((char)(a1 + step * i)).ToString()).ToArray(); }
        return res;
    }

    // ------------------------------------------------------------------ grid detection

    static List<(int A, int B)> Clusters(IEnumerable<int> v, int gap)
    {
        var o = new List<(int A, int B)>();
        foreach (var x in v) { if (o.Count > 0 && x - o[^1].B <= gap) o[^1] = (o[^1].A, x); else o.Add((x, x)); }
        return o;
    }

    /// <summary>The longest run of tick centres with an even spacing (a missing tick is allowed): first centre, spacing and cell count.</summary>
    static (double First, double Gap, int Cells)? Chain(List<double> centres)
    {
        centres.Sort(); (double First, double Gap, int Cells, int Len)? best = null;
        var gaps = centres.Zip(centres.Skip(1), (a, b) => b - a).Where(d => d > 20).Select(d => Math.Round(d)).Distinct().ToList();
        foreach (var gp in gaps)
            for (var i = 0; i < centres.Count; i++)
            {
                var run = new List<double> { centres[i] };
                for (var j = i + 1; j < centres.Count; j++)
                {
                    var d = centres[j] - run[^1]; var k = Math.Round(d / gp);
                    if (k is >= 1 and <= 2 && Math.Abs(d - k * gp) <= 0.08 * gp) run.Add(centres[j]); else if (d >= 2.5 * gp) break;
                }
                if (run.Count >= 5 && (best == null || run.Count > best.Value.Len))
                {
                    var cells = (int)Math.Round((run[^1] - run[0]) / gp);
                    best = (run[0], (run[^1] - run[0]) / cells, cells, run.Count);
                }
            }
        return best == null ? null : (best.Value.First, best.Value.Gap, best.Value.Cells);
    }

    /// <summary>Tick marks sit in a narrow band between two long border lines. One band per side: returns the best regular chain found.</summary>
    static (double First, double Gap, int Cells)? Strip(bool[] dark, int w, int h, bool alongX, bool fromFar, double lineFrac)
    {
        var along = alongX ? w : h; var across = alongX ? h : w;
        bool D(int a, int c) { var cc = fromFar ? across - 1 - c : c; return alongX ? dark[cc * w + a] : dark[a * w + cc]; }
        var lim = (int)(across * 0.2); var hits = new List<int>();
        for (var c = 0; c < lim; c++) { var n = 0; for (var a = 0; a < along; a++) if (D(a, c)) n++; if (n > lineFrac * along) hits.Add(c); }
        var lines = Clusters(hits, 2);
        (double First, double Gap, int Cells)? best = null;
        for (var i = 0; i + 1 < lines.Count; i++)
        {
            var c0 = lines[i].B + 1; var c1 = lines[i + 1].A - 1; var t = c1 - c0 + 1;
            if (t < 4 || t > 60) continue;
            var cs = new List<int>();
            for (var a = 0; a < along; a++) { var n = 0; for (var c = c0; c <= c1; c++) if (D(a, c)) n++; if (n >= 0.7 * t) cs.Add(a); }
            var centres = Clusters(cs, 2).Select(q => (q.A + q.B) / 2.0).ToList();
            if (centres.Count < 5) continue;
            var ch = Chain(centres);
            if (ch != null && (best == null || ch.Value.Cells > best.Value.Cells)) best = ch;
        }
        return best;
    }

    static (int Cols, int Rows, double[] ColEdgesPx, double[] RowEdgesPxTopDown) DetectGrid(SKBitmap bmp)
    {
        var w = bmp.Width; var h = bmp.Height; var dark = new bool[w * h];
        var px = bmp.Pixels;
        for (var i = 0; i < px.Length; i++) { var c = px[i]; var gray = (c.Red * 299 + c.Green * 587 + c.Blue * 114) / 1000; dark[i] = c.Alpha > 128 && gray < 215; }
        // columns: bands along the top and bottom edges (the ticks stand across them); rows: along the left and right edges
        var tops = new[] { Strip(dark, w, h, true, false, 0.35), Strip(dark, w, h, true, true, 0.35) };
        var sides = new[] { Strip(dark, w, h, false, false, 0.35), Strip(dark, w, h, false, true, 0.35) };
        var bc = tops.Where(x => x != null).OrderByDescending(x => x!.Value.Cells).FirstOrDefault();
        var br = sides.Where(x => x != null).OrderByDescending(x => x!.Value.Cells).FirstOrDefault();
        if (bc == null || br == null) return (0, 0, Array.Empty<double>(), Array.Empty<double>());
        var colEdges = Enumerable.Range(0, bc.Value.Cells + 1).Select(i => bc.Value.First + i * bc.Value.Gap).ToArray();
        var rowEdges = Enumerable.Range(0, br.Value.Cells + 1).Select(i => br.Value.First + i * br.Value.Gap).ToArray();
        return (bc.Value.Cells, br.Value.Cells, colEdges, rowEdges);
    }
}
