using ClosedXML.Excel;
using Pec.Qc.Core.OfficeKit;
using Pec.Qc.Core.Sources;

namespace Pec.Qc.Tests;

public class OfficeKitTests
{
    [Fact] public void Excel_is_created_edited_and_read_back_with_formulas_calculated()
    {
        using var t = new TempDir(); var f = System.IO.Path.Combine(t.Path, "a.xlsx");
        ExcelKit.Create(f, """[{"name":"Data","rows":[["Tag","Qty"],["P-101",2],["P-102",3],["Total","=SUM(B2:B3)"]]}]""", false);
        var info = ExcelKit.Inspect(f);
        Assert.Equal("Data", info[0].Name); Assert.Equal(4, info[0].Rows); Assert.Equal(1, info[0].Formulas);
        var (saved, done, bak) = ExcelKit.Edit(f, """
            [{"op":"set","sheet":"Data","cell":"B2","value":10},
             {"op":"append","sheet":"Data","rows":[["P-103",5]]},
             {"op":"style","sheet":"Data","range":"A1:B1","bold":true,"fill":"#FFEE99"},
             {"op":"addSheet","name":"Notes"},{"op":"renameSheet","sheet":"Notes","to":"Remarks"}]
            """, null);
        Assert.Equal(f, saved); Assert.Equal(5, done.Count); Assert.True(File.Exists(bak));
        using var wb = new XLWorkbook(f);
        Assert.Equal(10, wb.Worksheet("Data").Cell("B2").GetDouble());
        Assert.Equal(13, wb.Worksheet("Data").Cell("B4").GetDouble());          // SUM(B2:B3) after the edit
        Assert.Equal("P-103", wb.Worksheet("Data").Cell("A5").GetString());
        Assert.True(wb.TryGetWorksheet("Remarks", out _));
        Assert.Contains("13", System.Text.Json.JsonSerializer.Serialize(ExcelKit.Read(f, "Data", null, 50, false)));
    }

    [Fact] public void A_failing_excel_operation_saves_nothing_and_says_which()
    {
        using var t = new TempDir(); var f = System.IO.Path.Combine(t.Path, "a.xlsx");
        ExcelKit.Create(f, """[{"name":"S","rows":[["x"]]}]""", false);
        var before = File.ReadAllBytes(f);
        var e = Assert.Throws<ArgumentException>(() => ExcelKit.Edit(f, """[{"op":"set","sheet":"S","cell":"A1","value":1},{"op":"nope"}]""", null));
        Assert.Contains("Operation 2", e.Message); Assert.Equal(before, File.ReadAllBytes(f));
    }

    [Fact] public void Excel_create_will_not_replace_a_file_by_accident()
    {
        using var t = new TempDir(); var f = System.IO.Path.Combine(t.Path, "a.xlsx");
        ExcelKit.Create(f, """[{"name":"S","rows":[["x"]]}]""", false);
        Assert.Throws<IOException>(() => ExcelKit.Create(f, """[{"name":"S","rows":[["y"]]}]""", false));
        ExcelKit.Create(f, """[{"name":"S","rows":[["y"]]}]""", true);
    }

    [Fact] public void Word_is_created_edited_even_across_split_runs_and_read_back()
    {
        using var t = new TempDir(); var f = System.IO.Path.Combine(t.Path, "a.docx");
        WordKit.Create(f, """[{"type":"heading","level":1,"text":"Report"},{"type":"paragraph","text":"Pump P-101 is ready."},{"type":"bullets","items":["one","two"]},{"type":"table","rows":[["Tag","Qty"],["P-101","2"]]}]""", false);
        var text = OfficeText.Read(f).Text;
        Assert.Contains("# Report", text); Assert.Contains("P-101 is ready", text); Assert.Contains("Tag | Qty", text);
        var (_, done, bak) = WordKit.Edit(f, """[{"op":"replace","find":"P-101","replace":"P-202"},{"op":"append","blocks":[{"type":"paragraph","text":"Added line"}]},{"op":"setCell","table":1,"row":2,"column":2,"text":"9"}]""", null);
        Assert.Contains("2 place", done[0]); Assert.True(File.Exists(bak));
        var after = OfficeText.Read(f).Text;
        Assert.DoesNotContain("P-101", after); Assert.Contains("P-202", after); Assert.Contains("Added line", after); Assert.Contains("P-202 | 9", after);
    }

    [Fact] public void Pdf_is_created_read_merged_and_split()
    {
        using var t = new TempDir(); var a = System.IO.Path.Combine(t.Path, "a.pdf"); var b = System.IO.Path.Combine(t.Path, "b.pdf");
        PdfKit.Create(a, """[{"type":"heading","text":"Datasheet"},{"type":"paragraph","text":"Design pressure 10 bar"},{"type":"table","rows":[["Item","Value"],["Temp","80 C"]]},{"type":"pagebreak"},{"type":"paragraph","text":"Second page text"}]""", false, "Test");
        PdfKit.Create(b, """[{"type":"paragraph","text":"Other file"}]""", false, null);
        var read = System.Text.Json.JsonSerializer.Serialize(PdfKit.Read(a, null, 5000));
        Assert.Contains("Design pressure 10 bar", read); Assert.DoesNotContain("DatasheetDesign", read); Assert.Contains("Second page text", read);
        var m = PdfKit.Merge([a, b], System.IO.Path.Combine(t.Path, "m.pdf"), false);
        Assert.Equal(3, m.Pages);
        var x = PdfKit.Extract(m.Saved, "2-3", System.IO.Path.Combine(t.Path, "x.pdf"), false);
        Assert.Equal(2, x.Pages);
        Assert.Contains("Other file", System.Text.Json.JsonSerializer.Serialize(PdfKit.Read(x.Saved, null, 5000)));
    }

    [Fact] public void A_long_pdf_paragraph_wraps_onto_more_pages()
    {
        using var t = new TempDir(); var f = System.IO.Path.Combine(t.Path, "long.pdf");
        var words = string.Join(" ", Enumerable.Range(0, 4000).Select(i => "word" + i));
        PdfKit.Create(f, "[{\"type\":\"paragraph\",\"text\":\"" + words + "\"}]", false, null);
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(f);
        Assert.True(pdf.NumberOfPages > 3);
        Assert.Contains("word3999", System.Text.Json.JsonSerializer.Serialize(PdfKit.Read(f, null, 300_000)));
    }
}
