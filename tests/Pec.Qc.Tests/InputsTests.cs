using Pec.Qc.Core.Findings;
using Pec.Qc.Core.Inputs;
using Pec.Qc.Core.Knowledge;
using Pec.Qc.Core.OfficeKit;

namespace Pec.Qc.Tests;

/// <summary>A project inputs folder made of synthetic files: a PMS and a P&amp;ID as real PDFs, a line list as a real workbook.</summary>
internal sealed class InputsRig : IDisposable
{
    public readonly TempDir T = new();
    public string Inputs => System.IO.Path.Combine(T.Path, "inputs");
    public string IndexRoot => System.IO.Path.Combine(T.Path, "index");

    public InputsRig()
    {
        Directory.CreateDirectory(Inputs);
        PdfKit.Create(System.IO.Path.Combine(Inputs, "pms", "PMS-ACME.pdf"), """
            [{"type":"heading","text":"Piping Material Specification"},
             {"type":"paragraph","text":"Piping class B400A: austenitic stainless steel, 150 lb, 1 mm corrosion allowance."},
             {"type":"table","rows":[["Size","Component","Description"],["2 in","Gate valve","Class 150 flanged RF A351 CF8M bolted bonnet"],["2 in","Elbow 90","Long radius A403 WP304 SCH10S butt weld"]]},
             {"type":"pagebreak"},
             {"type":"heading","text":"Class F241S"},
             {"type":"paragraph","text":"Piping class F241S: carbon steel, 300 lb, NACE service."}]
            """, true, "PMS");
        PdfKit.Create(System.IO.Path.Combine(Inputs, "pid", "NVD-PRO-A160-FB-00002-10.pdf"), """[{"type":"paragraph","text":"P&ID sheet 10. Line 2\"-CD-60A07900-B400A-N-HS-017 from E-101 to V-201. Valve XV-1001."}]""", true, "P&ID");
        PdfKit.Create(System.IO.Path.Combine(Inputs, "vendor", "valve-datasheet.pdf"), """[{"type":"paragraph","text":"Gate valve XV-1001 data sheet. Face to face 178 mm, weight 14 kg."}]""", true, "Vendor");
        PdfKit.Create(System.IO.Path.Combine(Inputs, "scan.pdf"), """[{"type":"pagebreak"}]""", true, "blank");        // pages with no text: what a scan looks like
        Directory.CreateDirectory(System.IO.Path.Combine(Inputs, "line-list"));
        ExcelKit.Create(System.IO.Path.Combine(Inputs, "line-list", "LineList_Rev2.xlsx"), """
            [{"name":"Line List","header":false,"rows":[
              ["ACME Energy - Line list rev 2"],
              [],
              ["Line No.","Size (in)","Fluid","Piping Class","Design Pressure (psig)","Design Temp (degF)","Test Pressure (psig)","P&ID No.","Insulation","Remarks","Heat tracing"],
              ["2\"-CD-60A07900-B400A-N-HS-017","2","CD","B400A",200,325,300,"NVD-PRO-A160-FB-00002-10","N","two phase","no"],
              ["3\"-SC-60A05700-F241S-H-HS-002","3","SC","F241S",450,200,675,"NVD-PRO-A160-FB-00003-10","H","","yes"],
              ["2\"-CD-60A07900-B400A-N-HS-017","2","CD","B400A",200,325,300,"NVD-PRO-A160-FB-00002-10","N","duplicate row","no"]]}]
            """, true);
        File.WriteAllText(System.IO.Path.Combine(Inputs, "other", "notes.txt").Also(p => Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!)), "Kick-off notes: all lines are hydrotested with water.");
    }

    public InputsLibrary Lib => new(Inputs);
    public InputsIndex Index => new(Lib, IndexRoot);
    public void Dispose() => T.Dispose();
}

internal static class StringExt { public static string Also(this string s, Action<string> a) { a(s); return s; } }

public class InputKindTests
{
    [Theory]
    [InlineData("pms/anything.pdf", InputKind.Pms)] [InlineData("line-list/x.xlsx", InputKind.LineList)] [InlineData("pid/x.pdf", InputKind.Pid)]
    [InlineData("P&ID/x.pdf", InputKind.Pid)] [InlineData("vendor/x.pdf", InputKind.Vendor)] [InlineData("other/x.pdf", InputKind.Other)]
    [InlineData("loose/PMS_class_sheets.pdf", InputKind.Pms)] [InlineData("Piping Material Specification rev B.pdf", InputKind.Pms)]
    [InlineData("Line List rev2.xlsx", InputKind.LineList)] [InlineData("project_linelist.csv", InputKind.LineList)]
    [InlineData("P&ID 001.pdf", InputKind.Pid)] [InlineData("NVD-P-ID-01.pdf", InputKind.Pid)] [InlineData("Valve Data Sheet.pdf", InputKind.Vendor)]
    [InlineData("kickoff notes.txt", InputKind.Other)] [InlineData("pms.pdf", InputKind.Pms)]
    public void Files_are_classified_by_folder_then_by_name(string rel, InputKind expected) => Assert.Equal(expected, InputKinds.Classify(rel));

    [Fact]
    public void A_standard_folder_wins_over_the_words_in_the_file_name() => Assert.Equal(InputKind.Vendor, InputKinds.Classify("vendor/PMS-valve.pdf"));

    [Theory]
    [InlineData("iso", QcType.Isometric)] [InlineData("Isometric", QcType.Isometric)] [InlineData("piping isometric check", QcType.Isometric)]
    [InlineData("pid", QcType.Pid)] [InlineData("P&ID", QcType.Pid)] [InlineData("p&id check", QcType.Pid)] [InlineData("ga", QcType.Ga)] [InlineData("GA drawing", QcType.Ga)]
    [InlineData("general arrangement", QcType.Ga)]
    public void QC_types_are_understood_from_natural_names(string text, QcType expected) => Assert.Equal(expected, QcTypes.Parse(text));

    [Theory] [InlineData("")] [InlineData("hydro")] [InlineData(null)]
    public void Anything_else_is_not_a_QC_type(string? text) => Assert.Null(QcTypes.Parse(text));
}

public class InputsLibraryTests
{
    [Fact]
    public void The_inventory_lists_files_by_kind_and_leaves_out_noise()
    {
        using var r = new InputsRig();
        File.WriteAllText(System.IO.Path.Combine(r.Inputs, "pms", "~$PMS-ACME.pdf"), "x");
        File.WriteAllText(System.IO.Path.Combine(r.Inputs, "pms", "PMS-ACME.pdf.bak_20261124-101010"), "x");
        File.WriteAllText(System.IO.Path.Combine(r.Inputs, ".hidden"), "x");
        File.WriteAllText(System.IO.Path.Combine(r.Inputs, "pid", "drawing.dwg"), "x");
        var inv = r.Lib.Inventory();
        Assert.DoesNotContain(inv, d => d.RelPath.Contains("~$") || d.RelPath.Contains(".bak_") || d.RelPath.EndsWith(".hidden"));
        Assert.Equal(1, inv.Count(d => d.Kind == InputKind.LineList));
        Assert.Equal(1, inv.Count(d => d.Kind == InputKind.Vendor));
        Assert.False(inv.Single(d => d.RelPath.EndsWith("drawing.dwg")).Indexable);       // a DWG cannot be searched: it says so
        Assert.True(inv.Single(d => d.RelPath.EndsWith("PMS-ACME.pdf")).Indexable);
    }

    [Fact]
    public void Coverage_says_what_each_QC_type_still_needs()
    {
        using var r = new InputsRig();
        var iso = r.Lib.Coverage(QcType.Isometric);
        Assert.Equal([InputKind.Pms, InputKind.LineList, InputKind.Pid, InputKind.Vendor], iso.Select(c => c.Kind).ToArray());
        Assert.All(iso, c => Assert.True(c.Present));
        File.Delete(System.IO.Path.Combine(r.Inputs, "vendor", "valve-datasheet.pdf"));
        Assert.False(r.Lib.Coverage(QcType.Isometric).Single(c => c.Kind == InputKind.Vendor).Present);
        Assert.Equal([InputKind.Pid], r.Lib.Coverage(QcType.Pid).Select(c => c.Kind).ToArray());
        Assert.Equal([InputKind.Pid, InputKind.Vendor], r.Lib.Coverage(QcType.Ga).Select(c => c.Kind).ToArray());
    }

    [Fact]
    public void A_missing_folder_is_an_empty_inventory_not_an_error()
    {
        var lib = new InputsLibrary(@"C:\no\such\inputs\folder");
        Assert.False(lib.Exists); Assert.Empty(lib.Inventory()); Assert.All(lib.Coverage(QcType.Isometric), c => Assert.False(c.Present));
    }

    [Fact]
    public void A_path_that_leaves_the_inputs_folder_is_never_resolved()
    {
        using var r = new InputsRig();
        File.WriteAllText(System.IO.Path.Combine(r.T.Path, "secret.txt"), "outside");
        Assert.NotNull(r.Lib.Resolve(@"pms\PMS-ACME.pdf"));
        Assert.Null(r.Lib.Resolve(@"..\secret.txt"));
        Assert.Null(r.Lib.Resolve(System.IO.Path.Combine(r.T.Path, "secret.txt")));
        Assert.Null(r.Lib.Resolve(@"pms\..\..\secret.txt"));
        Assert.Null(r.Lib.Resolve(""));
    }

    [Fact]
    public void The_standard_layout_has_a_folder_per_kind()
    {
        using var t = new TempDir();
        InputsLibrary.EnsureLayout(t.Path);
        foreach (var n in new[] { "pms", "line-list", "pid", "vendor", "other" }) Assert.True(Directory.Exists(System.IO.Path.Combine(t.Path, n)), n);
    }
}

public class Bm25TagTests
{
    [Fact]
    public void A_line_number_is_searchable_whole_and_by_its_parts()
    {
        var b = new Bm25<string>();
        b.Add("hit", "Line 2\"-CD-60A07900-B400A-N-HS-017 is in class B400A");
        b.Add("other", "Line 3\"-SC-60A05700-F241S-H-HS-002 is in class F241S");
        Assert.Equal("hit", b.Search("B400A").First().Item);
        Assert.Equal("hit", b.Search("60A07900").First().Item);
        Assert.Equal("other", b.Search("F241S").First().Item);
        Assert.Equal("hit", b.Search("2\"-CD-60A07900-B400A-N-HS-017").First().Item);        // the exact tag still ranks first
        Assert.Contains("v-101a", Bm25<string>.Tokens("Vessel V-101A"));
        Assert.Contains("101a", Bm25<string>.Tokens("Vessel V-101A"));
    }

    [Fact]
    public void Adding_many_chunks_stays_fast()
    {
        var b = new Bm25<int>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 20000; i++) b.Add(i, $"row {i} line L-{i}-X class B{i % 9}00A");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), sw.Elapsed.ToString());
    }
}

public class InputsIndexTests
{
    [Fact]
    public void A_search_answers_with_the_file_and_the_page_it_came_from()
    {
        using var r = new InputsRig();
        var idx = r.Index;
        var rep = idx.Refresh();
        Assert.True(rep.Chunks > 5);
        var hit = idx.Search("class B400A 2 in gate valve", max: 3).First();
        Assert.EndsWith("PMS-ACME.pdf", hit.File);
        Assert.Equal("Pms", hit.Kind);
        Assert.Equal(1, hit.Page);                           // the table is on page 1
        Assert.Contains("Gate valve", hit.Text);
        var f241s = idx.Search("class F241S carbon steel 300 lb").First();
        Assert.Equal(2, f241s.Page);                         // the second class is on page 2
        Assert.Equal("page 2", f241s.Loc);
    }

    [Fact]
    public void A_table_row_stays_on_one_line_with_its_cells_apart()
    {
        using var r = new InputsRig();
        var idx = r.Index; idx.Refresh();
        var (text, loc, _) = idx.ReadPassage(@"pms\PMS-ACME.pdf", 1);
        var row = text.Split('\n').First(l => l.Contains("Gate valve"));
        Assert.Contains("2 in", row); Assert.Contains("Class 150 flanged", row);          // size, component and description on the same line
        Assert.Contains(" | ", row);
        Assert.StartsWith("page 1 of", loc);
    }

    [Fact]
    public void The_line_list_is_searched_by_a_part_of_the_line_number_with_sheet_and_rows()
    {
        using var r = new InputsRig();
        var idx = r.Index; idx.Refresh();
        var hit = idx.Search("60A07900", InputKind.LineList).First();
        Assert.EndsWith("LineList_Rev2.xlsx", hit.File);
        Assert.Contains("sheet 'Line List' rows", hit.Loc);
        Assert.Contains("Design Pressure (psig)=200", hit.Text);        // a value comes with the name of its column
    }

    [Fact]
    public void The_kind_filter_keeps_other_documents_out_of_the_answer()
    {
        using var r = new InputsRig();
        var idx = r.Index; idx.Refresh();
        var all = idx.Search("XV-1001");
        Assert.Contains(all, h => h.Kind == "Pid"); Assert.Contains(all, h => h.Kind == "Vendor");
        Assert.All(idx.Search("XV-1001", InputKind.Vendor), h => Assert.Equal("Vendor", h.Kind));
    }

    [Fact]
    public void A_drawing_without_a_text_layer_is_reported_not_skipped_silently()
    {
        using var r = new InputsRig();
        var rep = r.Index.Refresh();
        Assert.Contains(rep.Unreadable, u => u.StartsWith("scan.pdf") && u.Contains("no text layer"));
        Assert.DoesNotContain(r.Index.Search("scan"), h => h.File == "scan.pdf");
    }

    [Fact]
    public void A_file_type_that_cannot_be_read_is_listed_as_not_indexable()
    {
        using var r = new InputsRig();
        File.WriteAllText(System.IO.Path.Combine(r.Inputs, "pid", "old.dwg"), "x");
        File.WriteAllText(System.IO.Path.Combine(r.Inputs, "pid", "photo.jpg"), "x");
        var rep = r.Index.Refresh();
        Assert.Contains(rep.NotIndexable, n => n.StartsWith(@"pid\old.dwg")); Assert.Contains(rep.NotIndexable, n => n.StartsWith(@"pid\photo.jpg"));
    }

    [Fact]
    public void A_refresh_reads_only_what_changed()
    {
        using var r = new InputsRig();
        var idx = r.Index;
        var first = idx.Refresh();
        Assert.True(first.Added >= 5);
        var again = r.Index.Refresh();                                  // a new object: the state comes from disk
        Assert.Equal(0, again.Added); Assert.Equal(0, again.Updated); Assert.Equal(0, again.Removed); Assert.Equal(first.Chunks, again.Chunks);

        var note = System.IO.Path.Combine(r.Inputs, "other", "notes.txt");
        File.WriteAllText(note, "Kick-off notes: the test medium is nitrogen at 3 bar.");
        File.SetLastWriteTime(note, DateTime.Now.AddMinutes(5));
        var changed = r.Index.Refresh();
        Assert.Equal(1, changed.Updated);
        Assert.Contains("nitrogen", r.Index.Search("nitrogen").First().Text);
        Assert.Empty(r.Index.Search("hydrotested"));                    // the old text is gone

        File.Delete(note);
        var removed = r.Index.Refresh();
        Assert.Equal(1, removed.Removed);
        Assert.Empty(r.Index.Search("nitrogen"));

        File.WriteAllText(System.IO.Path.Combine(r.Inputs, "other", "new.txt"), "Added later: the paint system is epoxy.");
        Assert.Equal(1, r.Index.Refresh().Added);
        Assert.Contains("epoxy", r.Index.Search("paint epoxy").First().Text);
    }

    [Fact]
    public void An_excel_file_open_in_Excel_can_still_be_read()
    {
        using var r = new InputsRig();
        var xl = System.IO.Path.Combine(r.Inputs, "line-list", "LineList_Rev2.xlsx");
        using var held = new FileStream(xl, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);      // as when a colleague has it open
        var rep = r.Index.Refresh();
        Assert.DoesNotContain(rep.Unreadable, u => u.Contains("LineList"));
    }

    [Fact]
    public void A_passage_of_a_sheet_comes_with_its_row_numbers()
    {
        using var r = new InputsRig();
        var idx = r.Index; idx.Refresh();
        var (text, loc, note) = idx.ReadPassage(@"line-list\LineList_Rev2.xlsx", sheet: "Line List", fromRow: 3, rows: 3);
        Assert.Contains("Line No.", text); Assert.Contains("60A07900", text); Assert.StartsWith("sheet 'Line List' rows 3-5", loc); Assert.Null(note);
        Assert.Throws<ArgumentException>(() => idx.ReadPassage(@"line-list\LineList_Rev2.xlsx", sheet: "Nope"));
    }

    [Fact]
    public void Reading_a_passage_outside_the_inputs_folder_is_refused()
    {
        using var r = new InputsRig();
        File.WriteAllText(System.IO.Path.Combine(r.T.Path, "secret.txt"), "outside");
        Assert.Throws<FileNotFoundException>(() => r.Index.ReadPassage(@"..\secret.txt"));
        Assert.Throws<FileNotFoundException>(() => r.Index.ReadPassage(@"pms\missing.pdf"));
    }

    [Fact]
    public void Two_projects_do_not_share_an_index()
    {
        using var a = new InputsRig(); using var b = new InputsRig();
        var shared = System.IO.Path.Combine(a.T.Path, "shared-index");
        var ia = new InputsIndex(a.Lib, shared); var ib = new InputsIndex(b.Lib, shared);
        File.WriteAllText(System.IO.Path.Combine(a.Inputs, "other", "only-a.txt"), "marker apple");
        ia.Refresh(); ib.Refresh();
        Assert.NotEmpty(ia.Search("apple")); Assert.Empty(ib.Search("apple"));
    }
}
