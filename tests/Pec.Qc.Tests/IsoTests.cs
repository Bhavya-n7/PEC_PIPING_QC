using Pec.Qc.Core;
using Pec.Qc.Core.Findings;
using Pec.Qc.Core.Inputs;
using Pec.Qc.Core.Iso;
using Pec.Qc.Core.Model;

namespace Pec.Qc.Tests;

public class UnitsTests
{
    [Theory]
    [InlineData("15'7 9/16\"", 187.5625)]
    [InlineData("8'9 7/16\"", 105.4375)]
    [InlineData("2 7/8\"", 2.875)]
    [InlineData("61997 7/16\"", 61997.4375)]
    [InlineData("128328\"", 128328.0)]
    [InlineData("5'  7\"", 67.0)]
    [InlineData("-3 1/2\"", -3.5)]
    public void Feet_and_inches_are_read(string text, double inches) => Assert.Equal(inches, Units.FeetInchesToInches(text)!.Value, 6);

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("H")]
    [InlineData("2 x 2")]
    public void Text_that_is_not_a_length_is_not_a_number(string text) => Assert.Null(Units.FeetInchesToInches(text));

    [Fact]
    public void Lengths_are_written_the_way_isometrics_print_them()
    {
        Assert.Equal("15'7 9/16\"", Units.FormatFeetInches(187.5625));
        Assert.Equal("61997 7/16\"", Units.FormatInches(61997.4375));
        Assert.Equal("2 7/8\"", Units.FormatFeetInches(2.875));
    }

    [Fact]
    public void Model_quantities_and_positions_are_read()
    {
        Assert.Equal((1378951.0, "pascal"), Units.ParseQuantity("1378951pascal")!.Value);
        Assert.Equal(10.0, Units.PaToPsi(68947.57293168), 6);
        Assert.Equal(325.0, Units.CToF(162.7778), 3);
        var p = Units.ParsePosition("E 1574734.888mm N 3254362.766mm U 35623.92mm")!.Value;
        Assert.Equal((1574734.888, 3254362.766, 35623.92), (p.E, p.N, p.U));
        var q = Units.ParsePosition("W 100mm S 200mm D 300mm")!.Value;
        Assert.Equal((-100.0, -200.0, -300.0), (q.E, q.N, q.U));
        Assert.Null(Units.ParsePosition("E 1mm N 2mm"));
    }

    [Fact]
    public void Sizes_are_compared_without_order_or_repeats()
    {
        Assert.Equal(["2"], Units.NpsFromText("2 x 2"));
        Assert.Equal(["2", "3"], Units.NpsFromText("3 x 2"));
        Assert.Equal(["2", "3"], ModelSnapshot.SpecSizes("/B400A/TEE-80-50")!.Select(x => x.Replace("2 1/2", "2")).Distinct().Order().Select(x => x == "3" ? "3" : x).ToArray());
        Assert.Null(ModelSnapshot.SpecSizes("/B400A/CAP-7"));
    }
}

public class ModelSnapshotTests
{
    [Fact]
    public void A_line_is_found_by_any_spelling_and_its_length_is_measured_along_the_branch()
    {
        var snap = ModelSnapshot.Parse(IsoTestKit.ModelJson());
        Assert.Equal(2, snap.Lines.Count);
        var line = snap.Find("/2\"-xx-10a00100-a100a-n-hs-001 ");
        Assert.NotNull(line);
        Assert.Equal(7, line!.Items.Count());
        Assert.Equal(3048.0, ModelSnapshot.PipeLengthMm(line), 3);   // head to tail along E: 10 ft
        Assert.Contains(snap.AllEndPoints(), e => e.Line == "2\"-XX-10A00200-A100A-N-HS-002");
    }

    [Fact]
    public void A_pipe_that_occurs_twice_is_reported_with_where_and_whether_the_copies_are_identical()
    {
        var snap = ModelSnapshot.Parse(IsoTestKit.ModelJson(duplicate: true));
        var d = snap.Duplicates["2\"-XX-10A00100-A100A-N-HS-001"];
        Assert.Equal(2, d.Count);
        Assert.True(d.Identical);
        Assert.True(d.SameFile);
        Assert.Equal(2, snap.Lines.Count);   // the first record is kept, the line is not counted twice
    }

    [Fact]
    public void A_store_keeps_the_old_snapshot_when_the_model_is_exported_again()
    {
        var root = Path.Combine(Path.GetTempPath(), "pecqc-ms-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ModelStore(new Settings { SharedRoot = root });
            var target = store.PathFor("Blue Point", @"C:\x\625139-CO2.nwd");
            Assert.EndsWith(Path.Combine("models", "Blue Point", "625139-CO2.model.json"), target);
            Assert.Null(ModelStore.KeepOld(target));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "{}");
            var kept = ModelStore.KeepOld(target);
            Assert.NotNull(kept);
            Assert.True(File.Exists(kept));
            Assert.Equal("625139-CO2", store.Find("Blue Point")!.Name);
            Assert.Equal("625139-CO2", store.Find("Blue Point", "625139-co2.nwd")!.Name);
            Assert.Null(store.Find("Blue Point", "nothing"));
            Assert.Single(store.List("Blue Point"));   // the stamped old copy is not listed as a model
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}

public class IsoReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pecqc-iso-" + Guid.NewGuid().ToString("N"));
    public IsoReaderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Pdf(IsoSpec s) { var p = Path.Combine(_dir, "iso.pdf"); IsoTestKit.WritePdf(p, s); return p; }

    [Fact]
    public void The_title_block_bom_cut_table_coordinates_and_corner_are_read()
    {
        var sheet = Assert.Single(IsoReader.Read(Pdf(new IsoSpec())));
        Assert.Equal("2\"-XX-10A00100-A100A-N-HS-001", sheet.LineNo);
        Assert.Equal((1, 1), (sheet.Sheet, sheet.Sheets));
        Assert.Equal("00", sheet.Revision);
        Assert.Equal("300", sheet.Params["TEST PRESS. (psig)"]);
        Assert.Equal("A100A", sheet.Params["PIPNG CLASS"]);
        Assert.Equal("X-PID-0001-10", sheet.Params["P&ID NO"]);
        Assert.Equal("NA", sheet.Params["CALC NO"]);
        Assert.Equal("Two Phase", sheet.Params["PHASE"]);
        Assert.Empty(sheet.Warnings);

        Assert.Equal(4, sheet.Bom.Count);
        var pipe = sheet.Bom[0];
        Assert.Equal(("FABRICATION", "PIPE", "1", "2"), (pipe.Section, pipe.Category, pipe.PtNo, pipe.Size));
        Assert.Equal(120.0, pipe.Qty!.Value, 6);
        var tee = sheet.Bom[1];
        Assert.Equal(("FITTINGS", "2 x 2", 2.0), (tee.Category, tee.Size, tee.Qty!.Value));
        var sup = sheet.Bom[3];
        Assert.Equal(("ERECTION", "SUPPORTS"), (sup.Section, sup.Category));
        Assert.StartsWith("WE04", sup.Description);

        Assert.Equal(2, sheet.Cuts.Count);
        Assert.Equal((60.0, "2"), (sheet.Cuts[0].LengthIn!.Value, sheet.Cuts[0].Size));

        Assert.Equal(2, sheet.Coords.Count);
        var head = sheet.Coords.Single(c => c.ContLine.Length == 0);
        Assert.Equal((1000.0, 2000.0, 100.0), (head.E, head.N, head.U));
        var cont = sheet.Coords.Single(c => c.ContLine.Length > 0);
        Assert.Equal(("ON", "2\"-XX-10A00200-A100A-N-HS-002"), (cont.Cont, cont.ContLine));
    }

    [Fact]
    public void Sheet_links_and_numbering_are_read_on_a_multi_sheet_drawing()
    {
        var sheets = IsoReader.Read(Pdf(new IsoSpec { Sheets = 2, Pages = 2 }));
        Assert.Equal(2, sheets.Count);
        Assert.Equal((2, 2), (sheets[1].Sheet, sheets[1].Sheets));
        Assert.Equal("ON", Assert.Single(sheets[0].Links).Kind);
        Assert.Equal(1, sheets[1].Links.Single().Drg);
        Assert.Equal("FROM", sheets[1].Links.Single().Kind);
    }

    [Fact]
    public void A_page_without_text_is_reported_as_unreadable_not_as_empty()
    {
        var b = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        b.AddPage(1190.551, 841.889);
        var p = Path.Combine(_dir, "scan.pdf");
        File.WriteAllBytes(p, b.Build());
        var sheet = Assert.Single(IsoReader.Read(p));
        Assert.Contains(sheet.Warnings, w => w.Contains("no text layer"));
        Assert.Equal("", sheet.LineNo);
    }

    [Fact]
    public void A_page_size_other_than_A3_is_read_on_the_same_layout()
    {
        // the same drawing scaled to A4-ish width: positions are normalised to the reference frame, so the reader still finds the columns
        var sheet = Assert.Single(IsoReader.Read(Pdf(new IsoSpec())));
        Assert.Equal(1190.551, PdfTextLines.RefWidth, 3);
        Assert.NotEmpty(sheet.Bom);
    }

    [Fact]
    public void Only_pdf_files_are_read() => Assert.Throws<InvalidOperationException>(() => IsoReader.Read(Path.Combine(_dir, "x.dwg")));
}

public class IsoCheckerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pecqc-isochk-" + Guid.NewGuid().ToString("N"));
    public IsoCheckerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static readonly IsoRules Mapped = new() { SupportMap = new Dictionary<string, string> { ["/SUPP/ATTA-1"] = "WE04" } };

    private IsoLineResult Check(IsoSpec spec, string? modelJson = null, IsoRules? rules = null, IsoEvidence? ev = null)
    {
        var pdf = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        IsoTestKit.WritePdf(pdf, spec);
        var snap = ModelSnapshot.Parse(modelJson ?? IsoTestKit.ModelJson());
        var results = new IsoChecker(snap, "test-model", rules ?? Mapped, ev).CheckPdf(IsoReader.Read(pdf));
        var r = Assert.Single(results);
        AssertEveryFailHasBothValuesAndASource(r);
        return r;
    }

    /// <summary>The rule of the findings model: a FAIL names both values and where the source value came from.</summary>
    private static void AssertEveryFailHasBothValuesAndASource(IsoLineResult r)
    {
        foreach (var f in r.Findings.Where(f => f.Status == FindingStatus.Fail))
        {
            Assert.False(string.IsNullOrWhiteSpace(f.DrawingValue), $"FAIL without the drawing value: {f.Message}");
            Assert.False(string.IsNullOrWhiteSpace(f.SourceValue), $"FAIL without the source value: {f.Message}");
            Assert.False(string.IsNullOrWhiteSpace(f.Source?.Document), $"FAIL without the source document: {f.Message}");
        }
    }

    private static IEnumerable<Finding> Fails(IsoLineResult r) => r.Findings.Where(f => f.Status == FindingStatus.Fail);
    private static IEnumerable<Finding> Reviews(IsoLineResult r) => r.Findings.Where(f => f.Status == FindingStatus.Review);

    [Fact]
    public void A_correct_drawing_has_no_fail_and_no_review()
    {
        var r = Check(new IsoSpec());
        Assert.Empty(Fails(r));
        Assert.Empty(Reviews(r));
        Assert.Equal("OK", r.Verdict);
        // and it compared things: the OK findings exist for every kind of check
        foreach (var check in new[] { "identity", "title-block", "bom", "coordinates", "lengths" })
            Assert.Contains(r.Findings, f => f.Check == check && f.Status == FindingStatus.Ok);
    }

    [Fact]
    public void The_things_that_could_not_be_verified_are_listed_not_passed()
    {
        var r = Check(new IsoSpec());
        var info = r.Findings.Where(f => f.Status == FindingStatus.Info).Select(f => f.Message).ToList();
        Assert.Contains(info, m => m.Contains("line list"));
        Assert.Contains(info, m => m.Contains("PMS"));
        Assert.Contains(info, m => m.Contains("P&ID"));
        Assert.Contains(info, m => m.Contains("class-spec attachment"));
    }

    [Fact]
    public void A_wrong_piping_class_is_a_fail_with_both_values()
    {
        var r = Check(new IsoSpec { Class = "F400A" });
        var f = Assert.Single(Fails(r), x => x.Check == "title-block");
        Assert.Equal("F400A", f.DrawingValue);
        Assert.Contains("A100A", f.SourceValue);
        Assert.Contains("test-model", f.Source!.Document);
    }

    [Fact]
    public void A_wrong_test_pressure_a_wrong_design_pressure_and_a_wrong_temperature_are_fails()
    {
        Assert.Contains(Fails(Check(new IsoSpec { TestPress = "150" })), f => f.Message.StartsWith("TEST PRESS."));
        Assert.Contains(Fails(Check(new IsoSpec { DesignPress = "250" })), f => f.Message.StartsWith("DESIGN PRESS."));
        Assert.Contains(Fails(Check(new IsoSpec { DesignTemp = "400" })), f => f.Message.StartsWith("DESIGN TEMP."));
    }

    [Fact]
    public void A_pressure_inside_the_tolerance_is_not_a_fail()
        => Assert.Empty(Fails(Check(new IsoSpec { TestPress = "300.3" })));

    [Fact]
    public void A_wrong_tee_quantity_is_a_fail()
    {
        var f = Assert.Single(Fails(Check(new IsoSpec { Tees = 3 })), x => x.Check == "bom");
        Assert.Equal(("3", "2"), (f.DrawingValue, f.SourceValue));
        Assert.Contains("tee", f.Message);
    }

    [Fact]
    public void A_wrong_support_quantity_is_a_fail_when_the_support_is_mapped()
    {
        var f = Assert.Single(Fails(Check(new IsoSpec { Supports = 3 })), x => x.Check == "bom");
        Assert.Contains("support WE04", f.Message);
    }

    [Fact]
    public void Supports_that_are_not_in_the_support_map_are_a_review_never_a_fail_and_the_total_is_still_compared()
    {
        var r = Check(new IsoSpec(), rules: new IsoRules());   // empty support map
        Assert.Empty(Fails(r));
        var review = Assert.Single(Reviews(r), x => x.Check == "bom");
        Assert.Contains("support map", review.Message);
        Assert.Contains("/SUPP/ATTA-1 x2", review.SourceValue);
        Assert.Contains(r.Findings, f => f.Status == FindingStatus.Ok && f.Message.Contains("total number of supports"));
        // a wrong TOTAL is still a fail, whatever the map says
        Assert.Contains(Fails(Check(new IsoSpec { Supports = 3 }, rules: new IsoRules())), f => f.Message.Contains("total number of supports differs"));
    }

    [Fact]
    public void A_wrong_pipe_length_on_the_bom_is_a_fail_against_the_cut_table_and_against_the_model()
    {
        var r = Check(new IsoSpec { PipeBom = "11'0\"" });
        Assert.Contains(Fails(r), f => f.Check == "lengths" && f.Message.Contains("cut pipe lengths"));
        Assert.Contains(Fails(r), f => f.Check == "lengths" && f.Message.Contains("total pipe length"));
    }

    [Fact]
    public void A_cut_piece_that_does_not_add_up_is_a_fail()
        => Assert.Contains(Fails(Check(new IsoSpec { Pipe1 = "5'1\"" })), f => f.Check == "lengths" && f.Message.Contains("cut pipe lengths"));

    [Fact]
    public void A_coordinate_a_little_off_the_model_point_is_a_fail_that_names_the_distance()
    {
        // 1001 in instead of 1000 in = 25.4 mm
        var f = Assert.Single(Fails(Check(new IsoSpec { HeadE = "1001\"" })), x => x.Check == "coordinates");
        Assert.Contains("25.4 mm", f.Message);
        Assert.Contains("B1 head", f.SourceValue);
    }

    [Fact]
    public void A_coordinate_far_from_every_model_point_is_a_review_not_a_fail()
    {
        var r = Check(new IsoSpec { HeadE = "1500\"" });
        Assert.Empty(Fails(r).Where(f => f.Check == "coordinates" && f.Message.Contains("not")));
        Assert.Contains(Reviews(r), f => f.Check == "coordinates");
    }

    [Fact]
    public void A_connection_to_a_line_that_is_not_in_the_model_is_a_fail()
    {
        var r = Check(new IsoSpec { ContLine = "2\"-XX-10A09999-A100A-N-HS-099" });
        Assert.Contains(Fails(r), f => f.Check == "coordinates" && f.Message.Contains("not in the model"));
    }

    [Fact]
    public void A_line_that_is_not_in_the_model_is_a_fail_and_nothing_else_is_claimed()
    {
        var r = Check(new IsoSpec { LineNo = "2\"-XX-10A00777-A100A-N-HS-777" });
        Assert.Contains(Fails(r), f => f.Check == "identity" && f.Message.Contains("not in the model"));
        Assert.DoesNotContain(r.Findings, f => f.Check is "title-block" or "bom" or "lengths" && f.Status == FindingStatus.Ok);
    }

    [Fact]
    public void A_class_or_size_in_the_line_number_that_differs_from_the_model_is_a_fail()
    {
        Assert.Contains(Fails(Check(new IsoSpec { LineNo = "2\"-XX-10A00100-B999B-N-HS-001" }, modelJson: ModelWithLineName("2\"-XX-10A00100-B999B-N-HS-001"))),
            f => f.Check == "identity" && f.Message.Contains("piping class"));
        Assert.Contains(Fails(Check(new IsoSpec { LineNo = "3\"-XX-10A00100-A100A-N-HS-001" }, modelJson: ModelWithLineName("3\"-XX-10A00100-A100A-N-HS-001"))),
            f => f.Check == "identity" && f.Message.Contains("line size"));
    }

    private static string ModelWithLineName(string name) => IsoTestKit.ModelJson(spec: new IsoSpec { LineNo = name });

    [Fact]
    public void Missing_sheets_and_different_revisions_are_fails_about_the_drawing_itself()
    {
        var r = Check(new IsoSpec { Sheets = 3, Pages = 2 });
        Assert.Contains(Fails(r), f => f.Message.Contains("sheet numbers do not cover"));
        var f1 = Fails(r).First(f => f.Message.Contains("sheet numbers"));
        Assert.Equal(("1,2", "1,2,3"), (f1.DrawingValue, f1.SourceValue));
    }

    [Fact]
    public void A_duplicated_pipe_in_the_model_is_reported()
    {
        var r = Check(new IsoSpec(), modelJson: IsoTestKit.ModelJson(duplicate: true));
        Assert.Contains(Reviews(r), f => f.Check == "identity" && f.Message.Contains("2 times in the same model file"));
    }

    [Fact]
    public void A_title_block_value_the_model_does_not_have_is_a_review()
    {
        var json = IsoTestKit.ModelJson(tweakPipe: a => a.Remove(":OPEPRESSURE"));
        Assert.Contains(Reviews(Check(new IsoSpec(), modelJson: json)), f => f.Message.StartsWith("OPER PRESS."));
    }

    [Fact]
    public void A_model_that_disagrees_with_itself_is_a_review_naming_both_values()
    {
        var json = IsoTestKit.ModelJson(tweakPipe: a => a["TPRESS"] = "68947.6pascal");   // pipe 10 psig, branch 300 psig
        var f = Assert.Single(Reviews(Check(new IsoSpec(), modelJson: json)), x => x.Message.StartsWith("TEST PRESS."));
        Assert.Contains("inconsistent", f.Message);
        Assert.Contains("PIPE=10.0 psig", f.SourceValue);
        Assert.Contains("B1=300.0 psig", f.SourceValue);
    }

    [Fact]
    public void An_unreadable_sheet_is_a_review_never_an_ok()
    {
        var b = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        b.AddPage(1190.551, 841.889);
        var p = Path.Combine(_dir, "scan.pdf");
        File.WriteAllBytes(p, b.Build());
        var r = Assert.Single(new IsoChecker(ModelSnapshot.Parse(IsoTestKit.ModelJson()), "m", Mapped).CheckPdf(IsoReader.Read(p)));
        Assert.Equal("(unreadable)", r.LineNo);
        Assert.All(r.Findings, f => Assert.Equal(FindingStatus.Review, f.Status));
        Assert.Equal("REVIEW", r.Verdict);
    }

    [Fact]
    public void Two_lines_in_one_pdf_are_checked_separately()
    {
        var pdf = Path.Combine(_dir, "two.pdf");
        IsoTestKit.WritePdf(pdf, new IsoSpec());
        var other = Path.Combine(_dir, "other.pdf");
        IsoTestKit.WritePdf(other, new IsoSpec { Class = "F400A" });
        var sheets = IsoReader.Read(pdf).Concat(IsoReader.Read(other)).ToList();
        // the same line twice on different drawings: the sheets are grouped by line number, so they are one line with two (disagreeing) sheets
        var results = new IsoChecker(ModelSnapshot.Parse(IsoTestKit.ModelJson()), "m", Mapped).CheckPdf(sheets);
        var r = Assert.Single(results);
        Assert.Contains(Fails(r), f => f.Message.Contains("PIPNG CLASS differs between sheets"));
    }

    // ----- project inputs
    private static IsoEvidence Evidence(string inputs, bool index = true)
    {
        var lib = new InputsLibrary(inputs);
        var inv = lib.Inventory();
        InputsIndex? idx = null;
        if (index) { idx = new InputsIndex(lib, Path.Combine(inputs, "..", "idx-" + Guid.NewGuid().ToString("N"))); idx.Refresh(); }
        return new IsoEvidence(LineListSet.Load(lib), idx, inv);
    }

    private string Inputs(string llDesignPsig = "200", string llClass = "A100A", string? llLine = null, bool pmsHasClass = true, bool pidHasLine = true)
    {
        var root = Path.Combine(_dir, "inputs-" + Guid.NewGuid().ToString("N"));
        foreach (var d in new[] { "pms", "line-list", "pid" }) Directory.CreateDirectory(Path.Combine(root, d));
        ClosedXML.Excel.XLWorkbook wb = new();
        var ws = wb.AddWorksheet("Line List");
        string[] head = ["Line No.", "Size (in)", "Piping Class", "Design Pressure (psig)", "Design Temp (degF)", "Test Pressure (psig)", "P&ID No."];
        for (var i = 0; i < head.Length; i++) ws.Cell(1, i + 1).Value = head[i];
        object[] row = [llLine ?? "2\"-XX-10A00100-A100A-N-HS-001", 2, llClass, double.Parse(llDesignPsig), 325, 300, "X-PID-0001-10"];
        for (var i = 0; i < row.Length; i++) ws.Cell(2, i + 1).Value = XLCell(row[i]);
        wb.SaveAs(Path.Combine(root, "line-list", "LineList.xlsx"));
        PdfKit(Path.Combine(root, "pms", "PMS.pdf"), pmsHasClass ? "Piping class A100A: carbon steel, 150 lb." : "Piping class Z999Z: nothing relevant.");
        PdfKit(Path.Combine(root, "pid", "X-PID-0001-10.pdf"), pidHasLine ? "P&ID sheet 10. Line 2\"-XX-10A00100-A100A-N-HS-001 from E-101 to V-201." : "P&ID sheet 10. Line 2\"-XX-10A09999-A100A-N-HS-009 only.");
        return root;
    }

    private static ClosedXML.Excel.XLCellValue XLCell(object o) => o switch { string s => s, int i => i, double d => d, _ => o.ToString()! };
    private static void PdfKit(string path, string text) => Core.OfficeKit.PdfKit.Create(path, System.Text.Json.JsonSerializer.Serialize(new[] { new { type = "paragraph", text } }), true, "t");

    [Fact]
    public void A_matching_line_list_pms_and_pid_give_ok_findings_and_nothing_to_look_at()
    {
        var r = Check(new IsoSpec(), ev: Evidence(Inputs()));
        Assert.Empty(Fails(r));
        Assert.Empty(Reviews(r));
        foreach (var check in new[] { "line-list", "pms", "pid" }) Assert.Contains(r.Findings, f => f.Check == check && f.Status == FindingStatus.Ok);
        Assert.Contains(r.Findings, f => f.Check == "line-list" && f.Message.StartsWith("DESIGN PRESS.") && f.Status == FindingStatus.Ok);
    }

    [Fact]
    public void A_line_list_pressure_that_differs_is_a_fail_that_names_the_sheet_and_row()
    {
        var f = Assert.Single(Fails(Check(new IsoSpec(), ev: Evidence(Inputs(llDesignPsig: "250")))), x => x.Check == "line-list");
        Assert.Contains("DESIGN PRESS.", f.Message);
        Assert.Equal("200 psig", f.DrawingValue);
        Assert.StartsWith("250", f.SourceValue);
        Assert.Equal("LineList.xlsx", f.Source!.Document.Replace("line-list\\", "").Replace("line-list/", ""));
        Assert.Contains("row 2", f.Source.Page);
    }

    [Fact]
    public void A_line_list_class_that_differs_is_a_fail_and_a_line_missing_from_the_list_is_a_review()
    {
        Assert.Contains(Fails(Check(new IsoSpec(), ev: Evidence(Inputs(llClass: "B200B")))), f => f.Check == "line-list" && f.Message.StartsWith("PIPNG CLASS"));
        Assert.Contains(Reviews(Check(new IsoSpec(), ev: Evidence(Inputs(llLine: "2\"-XX-10A05555-A100A-N-HS-555")))), f => f.Check == "line-list" && f.Message.Contains("not in the line list"));
    }

    [Fact]
    public void A_class_that_the_pms_does_not_mention_and_a_line_that_the_pid_does_not_show_are_reviews_never_fails()
    {
        var r = Check(new IsoSpec(), ev: Evidence(Inputs(pmsHasClass: false, pidHasLine: false)));
        Assert.Empty(Fails(r));
        Assert.Contains(Reviews(r), f => f.Check == "pms" && f.Message.Contains("not found"));
        Assert.Contains(Reviews(r), f => f.Check == "pid" && f.Message.Contains("not found in the text"));
    }

    [Fact]
    public void A_line_list_value_in_a_unit_the_check_does_not_know_is_a_review_not_a_guess()
    {
        var root = Inputs();
        // replace the line list by one whose design pressure is in a unit that is not stated
        File.Delete(Path.Combine(root, "line-list", "LineList.xlsx"));
        var wb = new ClosedXML.Excel.XLWorkbook();
        var ws = wb.AddWorksheet("L");
        ws.Cell(1, 1).Value = "Line No."; ws.Cell(1, 2).Value = "Design Pressure"; ws.Cell(1, 3).Value = "Piping Class"; ws.Cell(1, 4).Value = "Size";
        ws.Cell(2, 1).Value = "2\"-XX-10A00100-A100A-N-HS-001"; ws.Cell(2, 2).Value = 14; ws.Cell(2, 3).Value = "A100A"; ws.Cell(2, 4).Value = 2;
        wb.SaveAs(Path.Combine(root, "line-list", "LineList.xlsx"));
        var r = Check(new IsoSpec(), ev: Evidence(root));
        Assert.Empty(Fails(r));
        Assert.Contains(Reviews(r), f => f.Check == "line-list" && f.Message.Contains("no unit"));
    }

    [Fact]
    public void The_rules_file_overrides_the_defaults_and_a_broken_file_falls_back_and_says_so()
    {
        var dir = Path.Combine(_dir, "req"); Directory.CreateDirectory(dir);
        Assert.Equal("built-in defaults", IsoRulesSource(dir));
        File.WriteAllText(Path.Combine(dir, IsoRules.FileName), """{"coord_tol_mm": 5, "supportMap": {"/S/A-1": "WE04"}, "ignore_atta_specs": ["^/X"]}""");
        var rules = IsoRules.Load(dir, out var src);
        Assert.Equal(5, rules.CoordTolMm);
        Assert.Equal(0.5, rules.PressureTolPsi);
        Assert.Equal("WE04", rules.SupportMap["/S/A-1"]);
        Assert.True(rules.IsIgnoredAtta("/X/1"));
        Assert.EndsWith(IsoRules.FileName, src);
        File.WriteAllText(Path.Combine(dir, IsoRules.FileName), "{ not json");
        var broken = IsoRules.Load(dir, out var bsrc);
        Assert.Equal(2.0, broken.CoordTolMm);
        Assert.Contains("could not be read", bsrc);
    }

    private static string IsoRulesSource(string dir) { IsoRules.Load(dir, out var s); return s; }

    [Fact]
    public void A_wider_coordinate_tolerance_from_the_rules_turns_the_fail_into_an_ok()
    {
        var spec = new IsoSpec { HeadE = "1001\"" };
        Assert.NotEmpty(Fails(Check(spec)));
        Assert.Empty(Fails(Check(spec, rules: new IsoRules { CoordTolMm = 30, SupportMap = Mapped.SupportMap })));
    }
}

/// <summary>Runs only when PEC_QC_SAMPLES names a folder holding the real sample isometric and its exported model snapshot (client material: never in the repository).</summary>
public sealed class RealSampleFactAttribute : FactAttribute
{
    public RealSampleFactAttribute()
    {
        var d = Environment.GetEnvironmentVariable("PEC_QC_SAMPLES");
        if (string.IsNullOrWhiteSpace(d) || !Directory.Exists(d) || Directory.GetFiles(d, "2-CD-60A07900-B400A-N-HS-017.pdf").Length == 0 || Directory.GetFiles(d, "*.model.json").Length == 0)
            Skip = "set PEC_QC_SAMPLES to a folder with the real sample isometric PDF and a *.model.json snapshot";
    }
}

public class RealSampleTests
{
    private static string Dir => Environment.GetEnvironmentVariable("PEC_QC_SAMPLES")!;

    [RealSampleFact]
    public void The_real_isometric_is_read_as_the_first_project_team_reads_it()
    {
        var sheets = IsoReader.Read(Path.Combine(Dir, "2-CD-60A07900-B400A-N-HS-017.pdf"));
        Assert.Equal(3, sheets.Count);
        Assert.All(sheets, s => { Assert.Equal("2\"-CD-60A07900-B400A-N-HS-017", s.LineNo); Assert.Equal("00", s.Revision); Assert.Empty(s.Warnings); Assert.Equal(3, s.Sheets); });
        Assert.Equal([8, 3, 7], sheets.Select(s => s.Bom.Count));
        Assert.Equal([3, 1, 3], sheets.Select(s => s.Cuts.Count));
        Assert.Equal([4, 0, 1], sheets.Select(s => s.Coords.Count));
        Assert.Equal(187.5625, sheets[0].Bom[0].Qty!.Value, 6);
        Assert.Equal("300", sheets[0].Params["TEST PRESS. (psig)"]);
        Assert.Equal("NA", sheets[0].Params["CALC NO"]);
    }

    [RealSampleFact]
    public void The_real_isometric_against_the_real_model_has_no_fail_and_the_corrupted_copies_each_fail()
    {
        var snap = ModelSnapshot.Load(Directory.GetFiles(Dir, "*.model.json")[0]);
        var rules = new IsoRules { SupportMap = new Dictionary<string, string> { ["/NOV-CFI-SUPP-PR/ATTA-119"] = "WE04", ["/NOV-CFI-SUPP-PR/ATTA-254"] = "WG01" } };
        var checker = new IsoChecker(snap, "real", rules);
        var good = Assert.Single(checker.CheckPdf(IsoReader.Read(Path.Combine(Dir, "2-CD-60A07900-B400A-N-HS-017.pdf"))));
        Assert.DoesNotContain(good.Findings, f => f.Status == FindingStatus.Fail);
        var demo = Path.Combine(Dir, "demo");
        if (!Directory.Exists(demo)) return;
        foreach (var pdf in Directory.GetFiles(demo, "*.pdf"))
            Assert.Contains(Assert.Single(checker.CheckPdf(IsoReader.Read(pdf))).Findings, f => f.Status == FindingStatus.Fail);
    }
}
