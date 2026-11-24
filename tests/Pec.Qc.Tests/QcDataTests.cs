using ClosedXML.Excel;
using Pec.Qc.Core;
using Pec.Qc.Core.Findings;
using Pec.Qc.Core.Inputs;
using Pec.Qc.Core.OfficeKit;
using Pec.Qc.Core.Requirements;

namespace Pec.Qc.Tests;

public class LineListTests
{
    [Fact]
    public void The_header_is_found_below_title_rows_and_columns_are_mapped_with_their_units()
    {
        using var r = new InputsRig();
        var ll = LineListSet.Load(r.Lib);
        var sheet = Assert.Single(ll.Sheets);
        Assert.Equal(3, sheet.HeaderRow);
        Assert.Equal("Line No.", sheet.Mapped["LineNo"]);
        Assert.Equal("Design Pressure (psig)", sheet.Mapped["DesignPressure"]);
        Assert.Equal("P&ID No.", sheet.Mapped["PidNo"]);
        Assert.Contains("Heat tracing", sheet.Unmapped);                         // not known: kept as an extra, not guessed
        Assert.Equal(3, ll.Entries.Count);
        var e = ll.Entries[0];
        Assert.Equal(4, e.Row);
        Assert.Equal(200, e.Fields["DesignPressure"].Number); Assert.Equal("psig", e.Fields["DesignPressure"].Unit);
        Assert.Equal("degF", e.Fields["DesignTemp"].Unit);
        Assert.Equal("B400A", e.Text("Class")); Assert.Equal("no", e.Extra["Heat tracing"]);
        Assert.Equal("NVD-PRO-A160-FB-00002-10", e.Text("PidNo"));
    }

    [Theory]
    [InlineData("2\"-CD-60A07900-B400A-N-HS-017")] [InlineData("/2\"-CD-60A07900-B400A-N-HS-017")] [InlineData(" 2\" - cd - 60a07900 - b400a - n - hs - 017 ")] [InlineData("2”-CD-60A07900-B400A-N-HS-017")]
    public void A_line_number_is_found_however_it_is_spelled(string spelling)
    {
        using var r = new InputsRig();
        var found = LineListSet.Load(r.Lib).Find(spelling);
        Assert.NotEmpty(found);
        Assert.All(found, e => Assert.Equal("2\"-CD-60A07900-B400A-N-HS-017", e.Key));
    }

    [Fact]
    public void A_part_of_a_line_number_finds_the_lines_that_contain_it_and_an_unknown_one_finds_nothing()
    {
        using var r = new InputsRig();
        var ll = LineListSet.Load(r.Lib);
        Assert.Single(ll.Find("SC-60A05700"));
        Assert.Empty(ll.Find("99\"-XX-0000"));
        Assert.Empty(ll.Find(""));
    }

    [Fact]
    public void A_line_listed_twice_is_reported()
    {
        using var r = new InputsRig();
        var dup = Assert.Single(LineListSet.Load(r.Lib).Duplicates());
        Assert.Equal(("2\"-CD-60A07900-B400A-N-HS-017", 2), dup);
    }

    [Fact]
    public void A_sheet_that_is_not_a_line_list_is_reported_and_not_read()
    {
        using var t = new TempDir();
        var f = System.IO.Path.Combine(t.Path, "line-list", "notes.xlsx");
        ExcelKit.Create(f, """[{"name":"Notes","header":false,"rows":[["Meeting"],["Item","Owner"],["a","b"],["c","d"],["e","f"]]}]""", true);
        var ll = LineListSet.Load(new InputsLibrary(t.Path));
        Assert.Empty(ll.Entries);
        Assert.Contains(ll.Warnings, w => w.Contains("no header row"));
    }

    [Fact]
    public void Filled_rows_without_a_line_number_are_counted_not_invented()
    {
        using var t = new TempDir();
        var f = System.IO.Path.Combine(t.Path, "line-list", "ll.xlsx");
        ExcelKit.Create(f, """[{"name":"S","header":false,"rows":[["Line Number","Size","Fluid","Class"],["L-1","2","CD","A1"],["","3","CD","A1"],["L-2","4","CD","A1"]]}]""", true);
        var ll = LineListSet.Load(new InputsLibrary(t.Path));
        Assert.Equal(2, ll.Entries.Count);
        Assert.Contains(ll.Warnings, w => w.Contains("1 filled row(s) without a line number"));
    }

    [Fact]
    public void A_csv_with_semicolons_and_a_quoted_cell_is_read()
    {
        using var t = new TempDir();
        t.File("line-list/ll.csv", "Line No;Size;Fluid;Piping Class;Remarks\nL-10;2;CD;B400A;\"two; with comma, and quote\"\"\"\nL-11;3;SC;F241S;ok\n");
        var ll = LineListSet.Load(new InputsLibrary(t.Path));
        Assert.Equal(2, ll.Entries.Count);
        Assert.Equal("two; with comma, and quote\"", ll.Entries[0].Text("Remarks"));
        Assert.Equal("F241S", ll.Entries[1].Text("Class"));
    }

    [Fact]
    public void Different_clients_header_spellings_map_to_the_same_fields()
    {
        using var t = new TempDir();
        var f = System.IO.Path.Combine(t.Path, "line-list", "other.xlsx");
        ExcelKit.Create(f, """[{"name":"LL","header":false,"rows":[["Pipeline Number","Nominal Size","Service","Pipe Class","Design Press. (barg)","Design Temperature (degC)","Hydrotest Pressure (barg)","P&ID","Insulation Type","Notes"],["PL-1","50","HC","A1A",15,120,22.5,"PID-001","P","x"]]}]""", true);
        var e = Assert.Single(LineListSet.Load(new InputsLibrary(t.Path)).Entries);
        Assert.Equal("PL-1", e.LineNo); Assert.Equal("A1A", e.Text("Class")); Assert.Equal(15, e.Fields["DesignPressure"].Number); Assert.Equal("barg", e.Fields["DesignPressure"].Unit);
        Assert.Equal(22.5, e.Fields["TestPressure"].Number); Assert.Equal("PID-001", e.Text("PidNo")); Assert.Equal("P", e.Text("Insulation")); Assert.Equal("HC", e.Text("Fluid"));
    }
}

public class RequirementTests
{
    private static RuleSource Src => new("ACME Piping Design Basis rev B", "4.2", "17");

    private static RequirementPack Pack(params RequirementRule[] rules) => new("ACME Energy", "1", false, "", rules);

    private static RequirementRule Fmt(string id = "ISO-LINE-FMT", string status = "confirmed", string severity = "FAIL") =>
        new(id, "iso", "identity", "Line numbers follow SIZE-FLUID-AREA-CLASS-INSULATION-SYSTEM-SEQ.", "pattern", "line number", @"^\d+(\s?\d/\d)?""-[A-Z]{2}-\w+-\w+-\w-\w+-\d+$", severity, status, Src);

    [Fact]
    public void A_rule_must_carry_the_document_and_the_section_or_page_it_comes_from()
    {
        Assert.Empty(RequirementValidation.Validate(Pack(Fmt())));
        var noSource = Fmt() with { Source = null };
        var onlyDoc = Fmt("B") with { Source = new RuleSource("ACME basis") };
        var noDoc = Fmt("C") with { Source = new RuleSource("", "4.2", "") };
        var errors = RequirementValidation.Validate(Pack(noSource, onlyDoc, noDoc));
        Assert.Equal(3, errors.Count(e => e.Contains("a source is required")));
    }

    [Fact]
    public void A_pack_with_other_mistakes_lists_each_of_them()
    {
        var errors = RequirementValidation.Validate(new RequirementPack("", "", false, "", new[]
        {
            Fmt("A"), Fmt("A"),                                                                            // the same id twice
            Fmt("B") with { Pattern = "(" },                                                               // not a regular expression
            Fmt("C") with { Pattern = "" },
            Fmt("D") with { Field = "" },
            Fmt("E") with { QcType = "hydro" }, Fmt("F") with { Severity = "MAYBE" }, Fmt("G") with { Status = "sure" }, Fmt("H") with { Kind = "magic" }, Fmt("I") with { Statement = "" },
        }));
        Assert.Contains(errors, e => e.Contains("client name")); Assert.Contains(errors, e => e.Contains("A: the id is used twice"));
        Assert.Contains(errors, e => e.Contains("B: the pattern is not a valid")); Assert.Contains(errors, e => e.Contains("C: a pattern rule needs a pattern"));
        Assert.Contains(errors, e => e.Contains("D: a pattern rule needs the field")); Assert.Contains(errors, e => e.Contains("E: qcType")); Assert.Contains(errors, e => e.Contains("F: severity"));
        Assert.Contains(errors, e => e.Contains("G: status")); Assert.Contains(errors, e => e.Contains("H: kind")); Assert.Contains(errors, e => e.Contains("I: the statement"));
    }

    [Fact]
    public void Saving_previews_first_then_writes_with_a_backup_of_the_file_it_replaces()
    {
        using var t = new TempDir();
        var store = new RequirementStore(t.Path);
        var preview = store.Save(Pack(Fmt()), apply: false);
        Assert.False(preview.Applied); Assert.False(File.Exists(store.FileFor("ACME Energy")));
        var first = store.Save(Pack(Fmt()), apply: true);
        Assert.True(first.Applied); Assert.True(File.Exists(store.FileFor("ACME Energy")));

        var second = store.Save(Pack(Fmt(), Fmt("ISO-2")), apply: true);
        Assert.NotNull(second.BackupPath); Assert.True(File.Exists(second.BackupPath!));
        Assert.Equal(1, store.Load("ACME Energy")!.AllRules.Count(r => r.Id == "ISO-LINE-FMT"));
        Assert.Equal(2, store.Load("acme energy")!.AllRules.Count);                      // the client name is not case sensitive
        Assert.Throws<ArgumentException>(() => store.Save(Pack(Fmt() with { Source = null }), apply: true));
    }

    [Fact]
    public void The_list_shows_packs_and_skips_the_checklist_file_and_broken_files()
    {
        using var t = new TempDir();
        var store = new RequirementStore(t.Path);
        store.Save(Pack(Fmt(), Fmt("C1", "candidate")), apply: true);
        t.File("qc-checklists.json", "{}"); t.File("broken.json", "{ nope");
        var s = Assert.Single(store.List());
        Assert.Equal("ACME Energy", s.Client); Assert.Equal(2, s.Rules); Assert.Equal(1, s.Candidates);
    }

    [Fact]
    public void Values_are_checked_against_the_pattern_and_a_broken_rule_names_its_source()
    {
        var pack = Pack(Fmt());
        var res = RequirementChecks.Evaluate(pack, QcType.Isometric, "Line Number", [("sheet 1", "2\"-CD-60A07900-B400A-N-HS-017"), ("sheet 2", "2 CD 60A07900")]);
        Assert.Equal([FindingStatus.Ok, FindingStatus.Fail], res.Select(f => f.Status).ToArray());
        var bad = res[1];
        Assert.Equal("2 CD 60A07900", bad.DrawingValue); Assert.Contains("must match", bad.SourceValue);
        Assert.Equal("ACME Piping Design Basis rev B", bad.Source!.Document); Assert.Contains("section 4.2", bad.Source.Page); Assert.Equal("ISO-LINE-FMT", bad.RuleId);
        Assert.Empty(FindingJson.Parse(FindingJson.ToJson(res), out var errs).Where(f => f.Status == FindingStatus.Fail && f.Source is null)); Assert.Empty(errs);   // a FAIL from a rule is a valid finding
    }

    [Fact]
    public void A_candidate_or_review_rule_never_produces_a_FAIL()
    {
        var bad = new[] { ("s", "garbage") };
        Assert.Equal(FindingStatus.Review, RequirementChecks.Evaluate(Pack(Fmt(status: "candidate")), QcType.Isometric, "line number", bad).Single().Status);
        Assert.Equal(FindingStatus.Review, RequirementChecks.Evaluate(Pack(Fmt(severity: "REVIEW")), QcType.Isometric, "line number", bad).Single().Status);
        Assert.Contains("candidate rule", RequirementChecks.Evaluate(Pack(Fmt(status: "candidate")), QcType.Isometric, "line number", bad).Single().Message);
    }

    [Fact]
    public void A_rule_applies_to_its_QC_type_and_its_field_only()
    {
        var pack = Pack(Fmt(), Fmt("ALL", "confirmed") with { QcType = "all", Field = "tag" });
        Assert.Empty(RequirementChecks.Evaluate(pack, QcType.Pid, "line number", [("s", "x")]));          // an iso rule is not applied to a P&ID
        Assert.Empty(RequirementChecks.Evaluate(pack, QcType.Isometric, "tag", [("s", "x")]).Where(f => f.RuleId == "ISO-LINE-FMT"));
        Assert.Single(RequirementChecks.Evaluate(pack, QcType.Ga, "tag", [("s", "x")]));                   // an 'all' rule is applied to every QC type
    }

    [Fact]
    public void Manual_rules_are_handed_to_the_person_not_tested()
    {
        var manual = new RequirementRule("M1", "pid", "notes", "Hold items are marked with a clouded triangle.", Source: Src);
        var pack = Pack(Fmt(), manual);
        Assert.Equal(["M1"], RequirementChecks.ManualRules(pack, QcType.Pid).Select(r => r.Id).ToArray());
        Assert.Empty(RequirementChecks.ManualRules(pack, QcType.Isometric));
        Assert.Empty(RequirementChecks.Evaluate(pack, QcType.Pid, "notes", [("s", "x")]));
    }

    [Fact]
    public void A_pack_survives_a_round_trip_through_JSON()
    {
        using var t = new TempDir();
        var store = new RequirementStore(t.Path);
        var pack = Pack(Fmt(), new RequirementRule("M1", "all", "x", "Statement", Source: Src));
        var back = RequirementStore.Parse(store.ToText(pack))!;
        Assert.Equal(pack.Client, back.Client); Assert.Equal(2, back.AllRules.Count); Assert.Equal("17", back.AllRules[0].Source!.Page); Assert.Equal(pack.AllRules[0].Pattern, back.AllRules[0].Pattern);
        Assert.Null(RequirementStore.Parse("{ not json"));
    }
}

public class FindingTests
{
    [Fact]
    public void A_complete_set_of_findings_is_accepted_whatever_the_capitalisation_of_the_names()
    {
        var f = FindingJson.Parse("""
            [{"Check":"bom","STATUS":"fail","subject":"2\"-CD-60A07900","message":"tee quantity differs","drawing":"4","source":"3","sourceDoc":"model","sourcePage":"line 017"},
             {"check":"title-block","status":"Review","subject":"line 017","message":"operating pressure is blank in the model"},
             {"status":"OK","message":"class matches"}, {"status":"info","message":"gaskets are not modelled"}]
            """, out var errors);
        Assert.Empty(errors);
        Assert.Equal([FindingStatus.Fail, FindingStatus.Review, FindingStatus.Ok, FindingStatus.Info], f.Select(x => x.Status).ToArray());
        Assert.Equal("line 017", f[0].Source!.Page); Assert.Equal("4", f[0].DrawingValue);
    }

    [Fact]
    public void A_FAIL_without_both_values_and_a_source_is_refused_with_the_reason()
    {
        FindingJson.Parse("""[{"status":"FAIL","subject":"x","message":"differs"}]""", out var e1);
        Assert.Contains(e1, e => e.Contains("value on the drawing")); Assert.Contains(e1, e => e.Contains("value in the source")); Assert.Contains(e1, e => e.Contains("source document"));
        FindingJson.Parse("""[{"status":"FAIL","subject":"x","message":"differs","drawing":"4","source":"3"}]""", out var e2);
        Assert.Single(e2); Assert.Contains("source document", e2[0]);
        FindingJson.Parse("""[{"status":"FAIL","message":"differs","drawing":"4","source":"3","sourceDoc":"d"}]""", out var e3);
        Assert.Contains(e3, e => e.Contains("needs the subject"));
        FindingJson.Parse("""[{"status":"REVIEW","message":"unclear"}]""", out var e4);
        Assert.Contains(e4, e => e.Contains("needs the subject"));
    }

    [Fact]
    public void Wrong_input_is_reported_not_thrown()
    {
        FindingJson.Parse("{ not json", out var a); Assert.Contains("not valid JSON", a[0]);
        FindingJson.Parse("""{"nothing":1}""", out var b); Assert.Contains("array of findings", b[0]);
        FindingJson.Parse("""[1, {"status":"MAYBE","message":"m"}, {"status":"OK"}]""", out var c);
        Assert.Contains(c, e => e.StartsWith("Finding 1")); Assert.Contains(c, e => e.Contains("Finding 2") && e.Contains("status must be")); Assert.Contains(c, e => e.Contains("Finding 3") && e.Contains("message"));
        Assert.Single(FindingJson.Parse("""{"findings":[{"status":"OK","message":"fine"}]}""", out var d)); Assert.Empty(d);
    }

    private static List<Finding> Sample() => FindingJson.Parse("""
        [{"check":"bom","status":"FAIL","subject":"2\"-CD-60A07900-B400A-N-HS-017","message":"tee quantity differs","drawing":"4","source":"3","sourceDoc":"625139 model","sourcePage":"TEE x3"},
         {"check":"title-block","status":"REVIEW","subject":"2\"-CD-60A07900-B400A-N-HS-017","message":"operating pressure blank in the model","drawing":"10"},
         {"check":"bom","status":"OK","subject":"2\"-CD-60A07900-B400A-N-HS-017","message":"elbows match"},
         {"check":"bom","status":"INFO","subject":"","message":"bolts are not modelled"}]
        """, out _);

    [Fact]
    public void The_verdict_is_the_worst_finding_and_no_findings_is_not_a_pass()
    {
        var all = Sample();
        Assert.Equal("FAIL", QcReport.Verdict(all));
        Assert.Equal("REVIEW", QcReport.Verdict(all.Where(f => f.Status != FindingStatus.Fail)));
        Assert.Equal("OK", QcReport.Verdict(all.Where(f => f.Status is FindingStatus.Ok or FindingStatus.Info)));
        Assert.Equal("NOT CHECKED", QcReport.Verdict([]));
        Assert.Equal(new FindingCounts(1, 1, 1, 1), QcReport.Counts(all));
    }

    [Fact]
    public void The_report_is_written_as_Excel_and_Markdown_in_the_project_folder_and_never_overwrites()
    {
        using var t = new TempDir();
        var s = new Settings { SharedRoot = t.Path };
        var info = new QcReportInfo("Blue Point piping", QcType.Isometric, "2-CD-60A07900-B400A-N-HS-017.pdf", "model.nwd", "Test Engineer (ZZ-T1)", new DateTime(2026, 11, 24, 10, 30, 0));
        var (xl, md) = QcReport.Write(s, info, Sample());
        Assert.Equal(System.IO.Path.Combine(t.Path, "reports", "Blue Point piping"), System.IO.Path.GetDirectoryName(xl));
        Assert.EndsWith("-iso-2-CD-60A07900-B400A-N-HS-017.xlsx", xl);
        var (xl2, md2) = QcReport.Write(s, info, Sample());                      // the same second: a new name, the first is kept
        Assert.NotEqual(xl, xl2); Assert.True(File.Exists(xl)); Assert.True(File.Exists(xl2)); Assert.True(File.Exists(md2));

        using var wb = new XLWorkbook(xl);
        Assert.Equal(["Summary", "Findings"], wb.Worksheets.Select(w => w.Name).ToArray());
        var ws = wb.Worksheet("Findings");
        Assert.Equal("FAIL", ws.Cell(2, 1).GetString());                         // the worst first
        Assert.Equal("4", ws.Cell(2, 5).GetString()); Assert.Equal("3", ws.Cell(2, 6).GetString()); Assert.Equal("625139 model", ws.Cell(2, 7).GetString());
        Assert.Equal("REVIEW", ws.Cell(3, 1).GetString());
        Assert.Equal("FAIL", wb.Worksheet("Summary").Cell(7, 2).GetString());
        var text = File.ReadAllText(md);
        Assert.Contains("**Verdict: FAIL**", text); Assert.Contains("| FAIL |", text); Assert.Contains("bolts are not modelled", text); Assert.Contains("A person decides", text);
    }

    [Fact]
    public void Without_a_shared_folder_reports_go_to_this_PCs_own_folder()
    {
        var s = new Settings();
        Assert.StartsWith(Settings.DataDirectory, QcReport.FolderFor(s, "Any project"));
        Assert.EndsWith("(no project)", QcReport.FolderFor(s, ""));
    }
}

public class ChecklistTests
{
    [Fact]
    public void Every_QC_type_has_a_default_checklist_with_unique_ids()
    {
        foreach (var t in Enum.GetValues<QcType>())
        {
            var items = QcChecklists.For(t, null, out var source);
            Assert.NotEmpty(items); Assert.Equal(items.Count, items.Select(i => i.Id).Distinct().Count()); Assert.Equal(QcChecklists.DefaultSource, source);
            Assert.All(items, i => Assert.StartsWith(QcTypes.Code(t).ToUpperInvariant() + "-", i.Id));
        }
        Assert.Equal(13, QcChecklists.Defaults[QcType.Isometric].Count);
    }

    [Fact]
    public void The_QC_lead_can_replace_a_checklist_and_a_broken_file_falls_back_to_the_default()
    {
        using var t = new TempDir();
        t.File(QcChecklists.FileName, """{"pid":[{"id":"PID-X","check":"c","text":"Our own item","against":"original"}]}""");
        var pid = QcChecklists.For(QcType.Pid, t.Path, out var src);
        Assert.Equal("PID-X", Assert.Single(pid).Id); Assert.Contains("shared", src);
        Assert.Equal(13, QcChecklists.For(QcType.Isometric, t.Path, out _).Count);                         // that type is not in the file: default
        t.File(QcChecklists.FileName, "{ nope");
        Assert.Equal(7, QcChecklists.For(QcType.Pid, t.Path, out _).Count);
    }
}
