using Pec.Qc.Core.OfficeKit;
using UglyToad.PdfPig.Writer;
using UglyToad.PdfPig.Core;

namespace Pec.Qc.Tests;

public class PdfMarkupTests
{
    /// <summary>A drawing-like A3 sheet: pipes (lines) with line numbers and tags beside them.</summary>
    static string Drawing(string dir, string name, params (string Text, double X, double Y)[] words)
    {
        var b = new PdfDocumentBuilder(); var page = b.AddPage(1191, 842);
        var font = b.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        for (var y = 100.0; y < 800; y += 60) page.DrawLine(new PdfPoint(40, y), new PdfPoint(1150, y), 1.2);
        for (var x = 100.0; x < 1150; x += 180) page.DrawLine(new PdfPoint(x, 40), new PdfPoint(x, 800), 1.2);
        foreach (var (t, x, y) in words) page.AddText(t, 8, new PdfPoint(x, y), font);
        var p = Path.Combine(dir, name); File.WriteAllBytes(p, b.Build()); return p;
    }

    [Fact] public void Clouds_highlights_comments_and_legend_are_real_annotations_and_comments_stay_clear_of_text_lines_and_each_other()
    {
        using var t = new TempDir();
        var src = Drawing(t.Path, "out.pdf", ("PSV-512F", 300, 130), ("1-1/2\"-P-1234-A1A", 500, 190), ("PSV-512G", 700, 250), ("XXX", 900, 310), ("NC", 160, 370));
        var dst = System.IO.Path.Combine(t.Path, "out_QC-Markup.pdf");
        var r = PdfMarkup.Apply(src, dst, """
            [{"type":"cloud","find":"PSV-512F","comment":"SET PRESSURE TO BE 150 PSIG AS PER RAW. OUTPUT SHOWS 120 PSIG.","color":"red"},
             {"type":"cloud","find":"1-1/2\"-P-1234-A1A","comment":"LINE SIZE TO BE 1-1/2 AS PER RAW. OUTPUT SHOWS 1/2.","color":"red"},
             {"type":"cloud","find":"PSV-512G","comment":"CONFIRM TAG WITH CLIENT.","color":"blue"},
             {"type":"highlight","find":"XXX"},
             {"type":"highlight","find":"NC"},
             {"type":"legend","lines":[{"text":"RED = CORRECTION REQUIRED","color":"red"},{"text":"BLUE = QUERY","color":"blue"},{"text":"YELLOW = UPDATED AS PER CLIENT MARKUP","color":"black"},{"text":"OK: TAGS CHECKED","color":"green"}]}]
            """, false);
        Assert.Empty(r.Warnings); Assert.Equal(3, r.Clouds); Assert.Equal(2, r.Highlights);
        Assert.Equal(3 + 2 + 3 * 2 + 1, r.Annotations);                       // clouds + highlights + (comment + arrow) x3 + legend

        // the file opens, has the annotations, and the drawing's own text is unchanged
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(dst); var pg = pdf.GetPage(1);
        var subtypes = pg.GetAnnotations().Select(a => a.Type.ToString()).ToList();
        Assert.Equal(3, subtypes.Count(s => s == "Polygon")); Assert.Equal(2, subtypes.Count(s => s == "Highlight")); Assert.Equal(4, subtypes.Count(s => s == "FreeText")); Assert.Equal(3, subtypes.Count(s => s == "Line"));
        Assert.Contains("PSV-512F", pg.Text);

        // geometry: every comment box (and the legend) is on no word, on no pipe, and not on another box
        var boxes = r.Placed.Select(o => o.Box).Select(b => new PdfMarkup.Box(b[0], b[1], b[2], b[3])).ToList();
        Assert.Equal(4, boxes.Count);
        var words = pg.GetWords().Select(w => new PdfMarkup.Box(w.BoundingBox.Left, w.BoundingBox.Bottom, w.BoundingBox.Right, w.BoundingBox.Top)).ToList();
        foreach (var b in boxes)
        {
            Assert.DoesNotContain(words, w => w.Overlaps(b));
            for (var y = 100.0; y < 800; y += 60) Assert.False(y > b.Y0 - 0.5 && y < b.Y1 + 0.5, $"a horizontal pipe at y={y} runs through the box {b}");
            for (var x = 100.0; x < 1150; x += 180) Assert.False(x > b.X0 - 0.5 && x < b.X1 + 0.5, $"a vertical pipe at x={x} runs through the box {b}");
        }
        for (var i = 0; i < boxes.Count; i++) for (var j = i + 1; j < boxes.Count; j++) Assert.False(boxes[i].Overlaps(boxes[j]));
    }

    [Fact] public void Find_returns_every_occurrence_with_its_box_and_phrases_work()
    {
        using var t = new TempDir();
        var src = Drawing(t.Path, "d.pdf", ("PSV-512F", 300, 130), ("PSV-512F", 600, 430), ("SET", 200, 500), ("PRESSURE", 230, 500));
        Assert.Equal(2, PdfMarkup.Find(src, "psv-512f").Count);
        var one = Assert.Single(PdfMarkup.Find(src, "SET PRESSURE"));
        Assert.True(one.Box.X0 <= 201 && one.Box.X1 >= 260);
        Assert.Empty(PdfMarkup.Find(src, "NOT-THERE"));
    }

    [Fact] public void A_missing_text_is_a_warning_not_a_silent_skip_and_a_repeat_can_be_marked_everywhere()
    {
        using var t = new TempDir();
        var src = Drawing(t.Path, "d.pdf", ("TK-A", 300, 130), ("TK-A", 700, 430));
        var r = PdfMarkup.Apply(src, System.IO.Path.Combine(t.Path, "o.pdf"), """[{"type":"cloud","find":"TK-A","all":true,"comment":"ARROW MISSING."},{"type":"cloud","find":"GHOST","comment":"X"}]""", false);
        Assert.Equal(2, r.Clouds); Assert.Contains(r.Warnings, w => w.Contains("GHOST") && w.Contains("not found"));
        var r2 = PdfMarkup.Apply(src, System.IO.Path.Combine(t.Path, "o2.pdf"), """[{"type":"cloud","find":"TK-A","comment":"X"}]""", false);
        Assert.Contains(r2.Warnings, w => w.Contains("occurs 2 times"));
    }

    [Fact] public void The_drawing_is_never_overwritten_and_an_existing_markup_needs_overwrite()
    {
        using var t = new TempDir(); var src = Drawing(t.Path, "d.pdf", ("A", 300, 130));
        Assert.Throws<IOException>(() => PdfMarkup.Apply(src, src, """[{"type":"highlight","rect":[290,125,320,140]}]""", true));
        var o = System.IO.Path.Combine(t.Path, "o.pdf"); PdfMarkup.Apply(src, o, """[{"type":"highlight","rect":[290,125,320,140]}]""", false);
        Assert.Throws<IOException>(() => PdfMarkup.Apply(src, o, """[{"type":"highlight","rect":[290,125,320,140]}]""", false));
        Assert.Throws<ArgumentException>(() => PdfMarkup.Apply(src, o, """[{"type":"cloud","comment":"x","color":"red"}]""", true));
        Assert.Throws<ArgumentException>(() => PdfMarkup.Apply(src, o, """[{"type":"cloud","rect":[1,1,5,5],"color":"pink"}]""", true));
    }

    [Fact] public void A_comment_with_no_free_space_is_reported_not_dropped_silently()
    {
        using var t = new TempDir();
        var b = new PdfDocumentBuilder(); var page = b.AddPage(120, 80);
        for (var y = 2.0; y < 80; y += 4) page.DrawLine(new PdfPoint(0, y), new PdfPoint(120, y), 1);          // a sheet that is all lines
        var src = System.IO.Path.Combine(t.Path, "full.pdf"); File.WriteAllBytes(src, b.Build());
        var r = PdfMarkup.Apply(src, System.IO.Path.Combine(t.Path, "o.pdf"), """[{"type":"cloud","rect":[40,30,60,40],"comment":"CHECK THIS ITEM PLEASE","color":"red"}]""", false);
        Assert.Equal(1, r.Clouds); Assert.Contains(r.Warnings, w => w.Contains("No free space"));
    }

    [Fact] public void A_zone_is_drawn_at_high_zoom_with_the_markup_on_it_and_the_whole_page_at_low_zoom()
    {
        using var t = new TempDir();
        var src = Drawing(t.Path, "d.pdf", ("PSV-512F", 300, 130));
        var o = System.IO.Path.Combine(t.Path, "o.pdf"); PdfMarkup.Apply(src, o, """[{"type":"cloud","find":"PSV-512F","comment":"CHECK.","color":"red"}]""", false);
        var (png, w, h) = PdfRender.Render(o, 1, new[] { 250.0, 100.0, 450.0, 200.0 }, 200, System.IO.Path.Combine(t.Path, "z.png"), false);
        Assert.InRange(w, 540, 570); Assert.InRange(h, 275, 285);              // 200 x 100 points at 200 dpi
        var bytes = File.ReadAllBytes(png); Assert.Equal(0x89, bytes[0]);
        using var bmp = SkiaSharp.SKBitmap.Decode(bytes);
        var red = 0; for (var x = 0; x < bmp.Width; x++) for (var y = 0; y < bmp.Height; y++) { var c = bmp.GetPixel(x, y); if (c.Red > 200 && c.Green < 80 && c.Blue < 80) red++; }
        Assert.True(red > 200, "the red cloud / comment are drawn in the picture");
        var (_, fw, fh) = PdfRender.Render(o, 1, null, 50, System.IO.Path.Combine(t.Path, "f.png"), false);
        Assert.InRange(fw, 800, 830);
        Assert.Throws<ArgumentException>(() => PdfRender.Render(o, 3, null, 50, System.IO.Path.Combine(t.Path, "g.png"), false));
    }
}
