using System.Text.Json;
using Pec.Qc.Core.Findings;
using Pec.Qc.Core.Ga;
using Pec.Qc.Core.Inputs;
using Pec.Qc.Core.Iso;
using Pec.Qc.Core.Model;

namespace Pec.Qc.Tests;

/// <summary>The piping plan flavour of a GA drawing (the real client sample): line numbers such as GH5174-2111-AOC-4"-11731009-NI with their elevation beside them, matchlines and the drawings it continues on.</summary>
public class GaLineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pecqc-galine-" + Guid.NewGuid().ToString("N"));
    public GaLineTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private const string L1 = "GH5174-2111-AOC-4\"-11731009-NI";
    private const string L2 = "GH5167-046-BA-0.75\"-11312003-C";
    private const string L3 = "GH5162-032-SCL-0.75\"-111321S3-PP(MG)";

    private static List<PidTestKit.T> Plan(Func<List<PidTestKit.T>, List<PidTestKit.T>>? tweak = null)
    {
        var t = new List<PidTestKit.T>
        {
            new(100, 100, L1), new(100, 108, "EL 102.615 m"),
            new(300, 200, L2), new(300, 208, "EL 101.405 m"),
            new(600, 300, L3, true), new(612, 300, "EL 104.033 m", true),                 // a rotated line number with its elevation beside it
            new(100, 700, "TIER ELEVATION : 105.000 m"), new(100, 712, "PLAN FROM 100.000 m TO 103.980 m"), new(900, 780, "SCALE : 1:75"),
            new(100, 60, "MATCHLINE  N  5316.500     CONT. ON DRAWING  1-GH5126-28-GAS-4296-00"), new(100, 80, "CONT. ON DRAWING  1-GH5140-28-GAS-4311-00"),
        };
        return tweak is null ? t : tweak(t);
    }

    private static string ModelJson(params (string Name, string Bore)[] pipes)
    {
        object Pipe(string name, string bore) => new
        {
            name = "/" + name, type = "PIPE", attrs = new Dictionary<string, string> { ["NAME"] = "/" + name, ["TYPE"] = "PIPE", ["BORE"] = bore }, path = "model.nwd > a.nwd > /AREA",
            branches = Array.Empty<object>(),
        };
        return JsonSerializer.Serialize(new { schema = 1, source = "x.nwd", totalItems = 10, lines = pipes.Select(p => Pipe(p.Name, p.Bore)).ToArray(), nozzles = Array.Empty<object>(), equipment = Array.Empty<object>() });
    }

    private (List<GaSheet> Sheets, string Path) Pdf(List<PidTestKit.T> items)
    {
        var p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        PidTestKit.WritePdf(p, [items]);
        return (GaReader.Read(p), p);
    }

    private IReadOnlyList<Finding> Check(string model, IsoEvidence? ev = null, List<PidTestKit.T>? items = null)
    {
        var (sheets, _) = Pdf(items ?? Plan());
        var f = new GaChecker(ModelSnapshot.Parse(model), "m", null, ev).Check(sheets);
        foreach (var x in f.Where(x => x.Status == FindingStatus.Fail))
        {
            Assert.False(string.IsNullOrWhiteSpace(x.DrawingValue)); Assert.False(string.IsNullOrWhiteSpace(x.SourceValue)); Assert.False(string.IsNullOrWhiteSpace(x.Source?.Document));
        }
        return f;
    }

    private static IEnumerable<Finding> Fails(IEnumerable<Finding> f) => f.Where(x => x.Status == FindingStatus.Fail);
    private static IEnumerable<Finding> Reviews(IEnumerable<Finding> f) => f.Where(x => x.Status == FindingStatus.Review);

    [Fact]
    public void Line_numbers_with_decimal_sizes_and_suffixes_elevations_references_matchlines_tier_and_plan_range_are_read()
    {
        var s = Assert.Single(Pdf(Plan()).Sheets);
        Assert.Equal(new[] { L1, L2, L3 }, s.Lines.Select(l => l.Text).Order().ToArray().Length == 3 ? s.Lines.Select(l => l.Text).OrderBy(x => Array.IndexOf(new[] { L1, L2, L3 }, x)).ToArray() : []);
        Assert.Equal(new double?[] { 4, 0.75, 0.75 }, s.Lines.OrderBy(l => Array.IndexOf(new[] { L1, L2, L3 }, l.Text)).Select(l => l.SizeIn).ToArray());
        Assert.Equal(new double?[] { 102615, 101405, 104033 }, s.Lines.OrderBy(l => Array.IndexOf(new[] { L1, L2, L3 }, l.Text)).Select(l => l.ElevationMm).ToArray());
        Assert.Contains("1-GH5126-28-GAS-4296-00", s.References);
        Assert.Contains("1-GH5140-28-GAS-4311-00", s.References);
        Assert.Single(s.Matchlines);
        Assert.Equal("TIER ELEVATION : 105.000 m", s.TierElevation);
        Assert.Equal("PLAN FROM 100.000 m TO 103.980 m", Assert.Single(s.PlanRanges));
        Assert.Equal("1:75", s.Scale);
    }

    [Fact]
    public void A_piping_plan_does_not_ask_for_equipment_tags()
    {
        var f = Check(ModelJson((L1, "100mm")));
        Assert.DoesNotContain(f, x => x.Check == "equipment" && x.Status == FindingStatus.Review);
    }

    [Fact]
    public void When_none_of_the_line_numbers_is_in_the_model_it_is_one_review_asking_if_it_is_the_right_model()
    {
        var f = Check(ModelJson(("2\"-CD-60A07900-B400A-N-HS-017", "50mm")));
        var r = Assert.Single(Reviews(f), x => x.Check == "lines");
        Assert.Contains("none of the 3 line numbers", r.Message);
        Assert.Empty(Fails(f));
    }

    [Fact]
    public void Line_numbers_found_in_the_model_are_counted_and_the_missing_ones_are_reviews()
    {
        var f = Check(ModelJson((L1, "100mm"), (L2, "20mm")));
        Assert.Contains(f, x => x.Check == "lines" && x.Status == FindingStatus.Ok && x.Message.StartsWith("2 of 3"));
        var r = Assert.Single(Reviews(f), x => x.Check == "lines");
        Assert.Equal(L3, r.Subject);
        Assert.Empty(Fails(f));
    }

    [Fact]
    public void A_size_in_the_line_number_that_differs_from_the_model_bore_is_a_fail()
    {
        var x = Assert.Single(Fails(Check(ModelJson((L1, "150mm"), (L2, "20mm"), (L3, "20mm")))));
        Assert.Equal(L1, x.Subject);
        Assert.Equal(("4\"", "6\""), (x.DrawingValue, x.SourceValue));
    }

    private IsoEvidence LineList(string size1 = "4", bool withL2 = true, bool withPid = false)
    {
        var root = Path.Combine(_dir, "inputs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "line-list"));
        var wb = new ClosedXML.Excel.XLWorkbook();
        var ws = wb.AddWorksheet("Line List");
        string[] head = ["Line No.", "Size (in)", "Piping Class", "Design Pressure (psig)"];
        for (var i = 0; i < head.Length; i++) ws.Cell(1, i + 1).Value = head[i];
        var rows = new List<(string, string)> { (L1, size1), (L3, "0.75") };
        if (withL2) rows.Add((L2, "0.75"));
        for (var r = 0; r < rows.Count; r++) { ws.Cell(r + 2, 1).Value = rows[r].Item1; ws.Cell(r + 2, 2).Value = double.Parse(rows[r].Item2, System.Globalization.CultureInfo.InvariantCulture); ws.Cell(r + 2, 3).Value = "X"; ws.Cell(r + 2, 4).Value = 100; }
        wb.SaveAs(Path.Combine(root, "line-list", "LL.xlsx"));
        if (withPid)
        {
            Directory.CreateDirectory(Path.Combine(root, "pid"));
            Core.OfficeKit.PdfKit.Create(Path.Combine(root, "pid", "P-1.pdf"), JsonSerializer.Serialize(new[] { new { type = "paragraph", text = $"P&ID. Line {L1} to the vessel. Line {L2}." } }), true, "t");
        }
        var lib = new InputsLibrary(root);
        InputsIndex? idx = null;
        if (withPid) { idx = new InputsIndex(lib, Path.Combine(_dir, "idx-" + Guid.NewGuid().ToString("N"))); idx.Refresh(); }
        return new IsoEvidence(LineListSet.Load(lib), idx, lib.Inventory());
    }

    [Fact]
    public void The_line_list_is_compared_line_by_line_with_the_size_in_inches()
    {
        var model = ModelJson((L1, "100mm"), (L2, "20mm"), (L3, "20mm"));
        var ok = Check(model, LineList());
        Assert.Empty(Fails(ok));
        Assert.Contains(ok, x => x.Check == "line-list" && x.Status == FindingStatus.Ok && x.Message.StartsWith("3 of 3"));
        var bad = Assert.Single(Fails(Check(model, LineList(size1: "6"))), x => x.Check == "line-list");
        Assert.Equal(L1, bad.Subject);
        Assert.Equal("4\"", bad.DrawingValue);
        Assert.StartsWith("6", bad.SourceValue);
        Assert.Contains("row 2", bad.Source!.Page);
        var missing = Check(model, LineList(withL2: false));
        Assert.Contains(Reviews(missing), x => x.Check == "line-list" && x.Subject == L2);
    }

    [Fact]
    public void Line_numbers_are_looked_up_in_the_pid_text_and_the_missing_ones_are_reviews_never_fails()
    {
        var f = Check(ModelJson((L1, "100mm"), (L2, "20mm"), (L3, "20mm")), LineList(withPid: true));
        Assert.Empty(Fails(f));
        Assert.Contains(f, x => x.Check == "tags" && x.Status == FindingStatus.Ok && x.Message.StartsWith("2 of 3"));
        Assert.Contains(Reviews(f), x => x.Check == "tags" && x.Subject == L3);
    }

    [Fact]
    public void Drawings_it_continues_on_that_are_not_in_the_inputs_are_listed_as_not_followed()
    {
        var f = Check(ModelJson((L1, "100mm")), LineList());
        var i = Assert.Single(f, x => x.Check == "drawing" && x.Status == FindingStatus.Info && x.Message.Contains("continues on"));
        Assert.Contains("1-GH5126-28-GAS-4296-00", i.Message);
        Assert.Contains("were not followed", i.Message);
    }

    [Fact]
    public void A_line_that_appears_with_two_sizes_or_an_elevation_outside_the_plan_range_is_not_reported_as_a_problem()
    {
        var f = Check(ModelJson((L1, "100mm"), (L2, "20mm"), (L3, "20mm")), items: Plan(t => { t.Add(new(100, 400, "GH5174-2111-AOC-6\"-11731009-NI")); t.Add(new(100, 408, "EL 109.500 m")); return t; }));
        Assert.DoesNotContain(f, x => x.Message.Contains("different size") || x.Message.Contains("plan range"));
    }
}

/// <summary>Runs only with PEC_QC_SAMPLES naming a folder that holds the real client GA drawing (client material: not in the repository).</summary>
public sealed class RealGaFactAttribute : FactAttribute
{
    public RealGaFactAttribute()
    {
        var d = Environment.GetEnvironmentVariable("PEC_QC_SAMPLES");
        if (string.IsNullOrWhiteSpace(d) || !File.Exists(Path.Combine(d, "1-GH5100-28-GAS-4297-00_00.pdf"))) Skip = "set PEC_QC_SAMPLES to a folder with 1-GH5100-28-GAS-4297-00_00.pdf";
    }
}

public class RealGaTests
{
    [RealGaFact]
    public void The_real_client_piping_plan_gives_every_line_number_its_elevation_and_the_references_and_tier_are_read()
    {
        var sheets = GaReader.Read(Path.Combine(Environment.GetEnvironmentVariable("PEC_QC_SAMPLES")!, "1-GH5100-28-GAS-4297-00_00.pdf"));
        Assert.Equal(8, sheets.Count);
        // counts of line numbers per sheet as an independent text extraction (another PDF library) finds them
        Assert.Equal(new[] { 85, 48, 61, 43, 34, 64, 111, 28 }, sheets.Select(s => s.Lines.Count).ToArray());
        Assert.All(sheets, s => Assert.All(s.Lines, l => { Assert.NotNull(l.SizeIn); Assert.NotNull(l.ElevationMm); }));
        Assert.All(sheets, s => { Assert.Equal("1:75", s.Scale); Assert.NotEmpty(s.TierElevation); Assert.NotEmpty(s.PlanRanges); Assert.NotEmpty(s.References); });
        Assert.Equal("TIER ELEVATION :105.000 m", sheets[0].TierElevation);
        Assert.Contains(sheets[0].Lines, l => l.Text == "GH5174-2111-AOC-4\"-11731009-NI" && Math.Abs(l.ElevationMm!.Value - 102615) < 1);
        Assert.Contains("1-GH5171-28-GAS-4300-00", sheets[0].References);
    }
}
