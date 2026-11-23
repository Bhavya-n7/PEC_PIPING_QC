using Pec.Qc.Core.Knowledge;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Pec.Qc.Tests;

public class TrainingPackTests
{
    /// <summary>A small PDF: a big bold heading, body text, a numbered step (must not become a heading), a second page with another heading.</summary>
    private static string MakePdf(string path)
    {
        var b = new PdfDocumentBuilder();
        var body = b.AddStandard14Font(Standard14Font.Helvetica);
        var bold = b.AddStandard14Font(Standard14Font.HelveticaBold);
        var p1 = b.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        p1.AddText("8. Weld Joint numbering", 20, new PdfPoint(40, 780), bold);
        var y = 740;
        foreach (var line in new[] { "Spool joints use the prefix J and erection joints use the prefix F.", "Joints are numbered in flow direction.", "Deleted joint numbers are never used again." })
        { p1.AddText(line, 10, new PdfPoint(40, y), body); y -= 14; }
        p1.AddText("4. Select Open to add the file to the project.", 10, new PdfPoint(40, y - 10), body);
        var p2 = b.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        p2.AddText("9. Fab shop numbering", 20, new PdfPoint(40, 780), bold);
        y = 740;
        foreach (var line in new[] { "The format is AAAAAA-BB-CD-EEEEE.", "AAAAAA is the job identifier.", "EEEEE is a running number." })
        { p2.AddText(line, 10, new PdfPoint(40, y), body); y -= 14; }
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    [Fact]
    public void Converts_a_pdf_with_real_headings_and_page_markers()
    {
        using var t = new TempDir();
        MakePdf(System.IO.Path.Combine(t.Path, "Client Procedure.pdf"));
        var pack = System.IO.Path.Combine(t.Path, "pack");
        var r = TrainingPack.Convert(t.Path, pack);

        Assert.Equal((1, 0, 0), (r.Converted, r.Unchanged, r.Failed));
        var doc = Assert.Single(r.Index.Documents);
        Assert.Equal("client-procedure", doc.Id);
        Assert.Equal(2, doc.Pages);
        Assert.Equal(["8. Weld Joint numbering", "9. Fab shop numbering"], doc.Sections.Select(s => s.Title));   // the numbered step is not a heading
        Assert.Equal([1, 2], doc.Sections.Select(s => s.Page));
        var md = File.ReadAllText(System.IO.Path.Combine(pack, doc.Markdown));
        Assert.Contains("<!-- page 2 -->", md);
        Assert.Contains("# 8. Weld Joint numbering", md.Replace("## ", "# "));
        Assert.Contains("4. Select Open", md);
        Assert.True(File.Exists(System.IO.Path.Combine(pack, TrainingPack.IndexFile)));
    }

    [Fact]
    public void Running_again_only_converts_what_changed()
    {
        using var t = new TempDir();
        var src = System.IO.Path.Combine(t.Path, "src"); Directory.CreateDirectory(src);
        MakePdf(System.IO.Path.Combine(src, "A.pdf"));
        File.WriteAllText(System.IO.Path.Combine(src, "notes.md"), "# Client notes\nSheet size A3.\n");
        var pack = System.IO.Path.Combine(t.Path, "pack");
        Assert.Equal(2, TrainingPack.Convert(src, pack).Converted);
        var again = TrainingPack.Convert(src, pack);
        Assert.Equal((0, 2), (again.Converted, again.Unchanged));
        File.WriteAllText(System.IO.Path.Combine(src, "notes.md"), "# Client notes\nSheet size A1.\n");     // new training arrives
        var third = TrainingPack.Convert(src, pack);
        Assert.Equal((1, 1), (third.Converted, third.Unchanged));
        Assert.Contains("A1", File.ReadAllText(System.IO.Path.Combine(pack, "docs", "notes.md")));
    }

    [Fact]
    public void Reads_one_section_or_a_page_range_without_loading_everything()
    {
        using var t = new TempDir();
        MakePdf(System.IO.Path.Combine(t.Path, "Client Procedure.pdf"));
        var pack = System.IO.Path.Combine(t.Path, "pack");
        TrainingPack.Convert(t.Path, pack);

        var sec = TrainingPack.ReadSection(pack, "client-procedure", "weld joint")!;
        Assert.Contains("prefix J", sec);
        Assert.DoesNotContain("Fab shop numbering", sec);          // stops at the next heading of the same level
        Assert.Contains("AAAAAA", TrainingPack.ReadPages(pack, "client-procedure", 2, 2)!);
        Assert.DoesNotContain("prefix J", TrainingPack.ReadPages(pack, "client-procedure", 2, 2)!);
        Assert.Null(TrainingPack.ReadSection(pack, "client-procedure", "nothing like this"));
        Assert.Null(TrainingPack.ReadPages(pack, "unknown", 1, 1));
    }
}
