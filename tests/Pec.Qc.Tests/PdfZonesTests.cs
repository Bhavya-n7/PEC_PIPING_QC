using Pec.Qc.Core.OfficeKit;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace Pec.Qc.Tests;

public class PdfZonesTests
{
    static string Drawing(string dir, string name, params (string Text, double X, double Y)[] words)
    {
        var b = new PdfDocumentBuilder(); var page = b.AddPage(1191, 842);
        var font = b.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        for (var y = 100.0; y < 800; y += 60) page.DrawLine(new PdfPoint(40, y), new PdfPoint(1150, y), 1.2);
        for (var x = 100.0; x < 1150; x += 180) page.DrawLine(new PdfPoint(x, 40), new PdfPoint(x, 800), 1.2);
        foreach (var (t, x, y) in words) page.AddText(t, 8, new PdfPoint(x, y), font);
        var p = Path.Combine(dir, name); File.WriteAllBytes(p, b.Build()); return p;
    }

    /// <summary>The same drawing with the page rotated (scanners and plotters often leave it so): all positions must be in the displayed orientation.</summary>
    static string Rotated(string src, string dst, int rot)
    {
        var d = PdfSharp.Pdf.IO.PdfReader.Open(src, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify); d.Pages[0].Rotate = rot; d.Save(dst); d.Dispose(); return dst;
    }

    [Theory] [InlineData(90)] [InlineData(180)] [InlineData(270)]
    public void On_a_rotated_page_find_marks_and_comments_follow_the_displayed_page(int rot)
    {
        using var t = new TempDir();
        var src = Rotated(Drawing(t.Path, "d.pdf", ("PSV-512F", 300, 130), ("LINE-1", 500, 190), ("TAG-3", 700, 250)), Path.Combine(t.Path, "r.pdf"), rot);
        var hit = Assert.Single(PdfMarkup.Find(src, "PSV-512F"));
        // the word is at (300,130) on the unrotated sheet (1191 x 842): where it is when displayed
        var (ex, ey) = rot switch { 90 => (130.0, 1191 - 300.0), 180 => (1191 - 300.0, 842 - 130.0), _ => (842 - 130.0, 300.0) };
        Assert.InRange((hit.Box.X0 + hit.Box.X1) / 2, ex - 40, ex + 40); Assert.InRange((hit.Box.Y0 + hit.Box.Y1) / 2, ey - 40, ey + 40);
        var o = Path.Combine(t.Path, "o.pdf");
        var r = PdfMarkup.Apply(src, o, """[{"type":"cloud","find":"PSV-512F","comment":"SET PRESSURE TO BE 150 PSIG AS PER RAW.","color":"red"},{"type":"cloud","find":"TAG-3","comment":"CONFIRM.","color":"blue"}]""", false);
        Assert.Empty(r.Warnings);
        // the cloud is drawn around the word as DISPLAYED (look at the picture of the displayed page)
        var (png, w, h) = PdfZones.Render(o, 1, new[] { ex - 60, ey - 40, ex + 60, ey + 40 }, 150, Path.Combine(t.Path, "z.png"), false);
        using var bmp = SkiaSharp.SKBitmap.Decode(File.ReadAllBytes(png));
        var red = 0; for (var x = 0; x < bmp.Width; x++) for (var y = 0; y < bmp.Height; y++) { var c = bmp.GetPixel(x, y); if (c.Red > 200 && c.Green < 90 && c.Blue < 90) red++; }
        Assert.True(red > 150, "the cloud is drawn around the word as displayed");
        // and the comment boxes (displayed space) cover no word
        var boxes = r.Placed.Select(p => p.Box).Select(b => new PdfMarkup.Box(b[0], b[1], b[2], b[3])).ToList();
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(src);
        var words = pdf.GetPage(1).GetWords().Select(wd => new PdfMarkup.Box(wd.BoundingBox.Left, wd.BoundingBox.Bottom, wd.BoundingBox.Right, wd.BoundingBox.Top)).ToList();
        foreach (var b in boxes) Assert.DoesNotContain(words, wd => wd.Overlaps(b));
    }

    [Fact] public void Marks_grow_with_the_sheet_so_an_A0_comment_is_as_readable_as_an_A3_one()
    {
        using var t = new TempDir();
        var a3 = Drawing(t.Path, "a3.pdf", ("PSV-512F", 300, 130));
        var b = new PdfDocumentBuilder(); var page = b.AddPage(3370, 2384); var font = b.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica); page.AddText("PSV-512F", 20, new PdfPoint(900, 400), font);
        var a0 = Path.Combine(t.Path, "a0.pdf"); File.WriteAllBytes(a0, b.Build());
        var r3 = PdfMarkup.Apply(a3, Path.Combine(t.Path, "o3.pdf"), """[{"type":"cloud","find":"PSV-512F","comment":"CHECK THIS TAG PLEASE.","color":"red"}]""", false);
        var r0 = PdfMarkup.Apply(a0, Path.Combine(t.Path, "o0.pdf"), """[{"type":"cloud","find":"PSV-512F","comment":"CHECK THIS TAG PLEASE.","color":"red"}]""", false);
        var w3 = r3.Placed[0].Box[2] - r3.Placed[0].Box[0]; var w0 = r0.Placed[0].Box[2] - r0.Placed[0].Box[0];
        Assert.InRange(w0 / w3, 2.3, 3.3);                                       // 3370 / 1191 = 2.83
    }

    static string Sheet(string dir, string name, double w, double h, int cols, int rows)
    {
        var b = new PdfDocumentBuilder(); var pg = b.AddPage(w, h);
        double mx = w * 0.03, my = h * 0.03, band = w * 0.012;
        var (l, r, bt, tp) = (mx, w - mx, my, h - my);
        foreach (var off in new[] { 0.0, band })
        {
            pg.DrawLine(new PdfPoint(l, tp - off), new PdfPoint(r, tp - off), 0.8); pg.DrawLine(new PdfPoint(l, bt + off), new PdfPoint(r, bt + off), 0.8);
            pg.DrawLine(new PdfPoint(l + off, bt), new PdfPoint(l + off, tp), 0.8); pg.DrawLine(new PdfPoint(r - off, bt), new PdfPoint(r - off, tp), 0.8);
        }
        for (var i = 1; i < cols; i++) { var x = l + band + (r - l - 2 * band) * i / cols; pg.DrawLine(new PdfPoint(x, tp), new PdfPoint(x, tp - band), 0.6); pg.DrawLine(new PdfPoint(x, bt), new PdfPoint(x, bt + band), 0.6); }
        for (var j = 1; j < rows; j++) { var y = bt + band + (tp - bt - 2 * band) * j / rows; pg.DrawLine(new PdfPoint(l, y), new PdfPoint(l + band, y), 0.6); pg.DrawLine(new PdfPoint(r, y), new PdfPoint(r - band, y), 0.6); }
        var p = Path.Combine(dir, name); File.WriteAllBytes(p, b.Build()); return p;
    }

    [Fact] public void The_sheets_own_grid_is_read_from_its_tick_marks_and_the_same_zones_serve_an_A3_scan_and_an_A0_redraw()
    {
        using var t = new TempDir();
        var a3 = Sheet(t.Path, "a3.pdf", 1191, 842, 17, 11); var a0 = Sheet(t.Path, "a0.pdf", 3370, 2384, 17, 11);
        var p3 = PdfZones.MakePlan(a3, 1, "17-1", "K-A"); var p0 = PdfZones.MakePlan(a0, 1, "17-1", "K-A");
        Assert.Equal("A3", p3.SheetClass); Assert.Equal("A0", p0.SheetClass);
        Assert.Equal((17, 11), (p3.Columns, p3.Rows)); Assert.Equal((17, 11), (p0.Columns, p0.Rows));      // the sheet's own grid, found from its tick marks
        Assert.Contains("detected", p3.GridSource); Assert.Contains("detected", p0.GridSource);
        Assert.Equal((3, 2), (p3.TilesAcross, p3.TilesDown)); Assert.Equal((4, 3), (p0.TilesAcross, p0.TilesDown));       // by sheet size
        var pair = PdfZones.MakePlan(a0, 1, "17-1", "K-A", 3, 2);                                           // the same tiles for both drawings of a pair
        Assert.Equal(p3.Tiles.Select(x => x.Cells), pair.Tiles.Select(x => x.Cells));
        var tl = p3.Tiles[0]; Assert.StartsWith("columns 17-", tl.Cells); Assert.Contains("rows K-", tl.Cells);
        Assert.All(p3.Tiles.Concat(p0.Tiles), x => Assert.InRange(x.Dpi, 40, 300));
        // tiles overlap by one cell and cover the whole sheet
        Assert.Equal(0, p3.Tiles.Min(x => x.Rect[0])); Assert.Equal(1191, p3.Tiles.Max(x => x.Rect[2]), 0);
        // a sheet with no border at all falls back to the ISO grid and says so
        var plain = new PdfDocumentBuilder(); plain.AddPage(1191, 842); var pp = Path.Combine(t.Path, "plain.pdf"); File.WriteAllBytes(pp, plain.Build());
        var pl = PdfZones.MakePlan(pp, 1); Assert.Contains("ISO 5457", pl.GridSource); Assert.Equal((8, 6), (pl.Columns, pl.Rows));
    }

    [Theory] [InlineData(420, 297, "A3")] [InlineData(297, 420, "A3")] [InlineData(1189, 841, "A0")] [InlineData(594, 420, "A2")] [InlineData(210, 297, "A4")] [InlineData(850, 600, "A1")]
    public void Sheet_size_class_is_read_from_the_page_size_in_either_orientation(double w, double h, string cls) => Assert.Equal(cls, PdfZones.SheetClass(w, h));
}
