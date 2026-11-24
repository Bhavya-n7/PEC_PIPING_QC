using System.Text.Json;
using Pec.Qc.Core.Findings;
using Pec.Qc.Core.Ga;
using Pec.Qc.Core.Inputs;
using Pec.Qc.Core.Iso;
using Pec.Qc.Core.Model;

namespace Pec.Qc.Tests;

public class GaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pecqc-ga-" + Guid.NewGuid().ToString("N"));
    public GaTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // two process items with a box, one with a meaningless AVEVA POS (as in a real model), and a duplicate record (an appended file)
    private static string ModelJson(bool withEquipment = true)
    {
        object Eq(string name, string pos, double[] box) => new { name, type = "EQUI", attrs = new Dictionary<string, string> { ["NAME"] = name, ["POS"] = pos }, path = "model.nwd > a.nwd > /SKID > /SKID-MECH", bboxMm = box };
        var eq = new List<object>
        {
            Eq("/60D302_CONDENSATE_POT", "E 1576982mm N 3253683mm U 32224mm", [1576180, 3252908, 31764, 1577784, 3254420, 34164]),
            Eq("/60E301_REGENERATION_GAS_HEATER", "E 1577000mm N 3257000mm U 36500mm", [1576616, 3254744, 36207, 1577806, 3260406, 38549]),
            Eq("/EARTHING-60U301", "E 1033mm N 5mm U 30488mm", [1574187, 3252156, 30905, 1579609, 3263430, 36689]),   // POS is nonsense; the box is right
            Eq("/60D302_CONDENSATE_POT", "E 1mm N 1mm U 1mm", [0, 0, 0, 1, 1, 1]),                                        // duplicate record: ignored
        };
        return JsonSerializer.Serialize(withEquipment
            ? new { schema = 1, source = "x.nwd", totalItems = 10, lines = Array.Empty<object>(), nozzles = Array.Empty<object>(), equipment = eq, typeCounts = new { EQUI = 4 } }
            : (object)new { schema = 1, source = "x.nwd", totalItems = 10, lines = Array.Empty<object>(), nozzles = Array.Empty<object>() });
    }

    private static string Inch(double mm) { var v = Math.Round(mm / 25.4 * 16) / 16; var whole = (int)v; var frac = (int)Math.Round((v - whole) * 16); return frac == 0 ? $"{whole}\"" : $"{whole} {frac / Gcd(frac, 16)}/{16 / Gcd(frac, 16)}\""; }
    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

    private static List<PidTestKit.T> Sheet(Func<List<PidTestKit.T>, List<PidTestKit.T>>? tweak = null, double shiftMm = 0, string tag = "60D302", double elevMm = 34164)
    {
        double cx = (1576180 + 1577784) / 2.0 + shiftMm, cy = (3252908 + 3254420) / 2.0, cz = (31764 + 34164) / 2.0;
        var t = new List<PidTestKit.T>
        {
            new(900, 780, "SCALE 1:50"), new(900, 792, "REV 0"), new(60, 60, "NORTH"), new(100, 780, "GENERAL ARRANGEMENT OF THE DEHYDRATION SKID PLAN"),
            new(150, 150, tag), new(150, 164, "E " + Inch(cx)), new(150, 173, "N " + Inch(cy)), new(150, 183, "U " + Inch(cz)), new(150, 210, $"EL +{elevMm:0}"),
            new(450, 150, "60E301"),
        };
        return tweak is null ? t : tweak(t);
    }

    private (List<GaSheet> Sheets, string Path) Pdf(List<PidTestKit.T> items)
    {
        var p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".pdf");
        PidTestKit.WritePdf(p, [items]);
        return (GaReader.Read(p), p);
    }

    private IReadOnlyList<Finding> Check(List<PidTestKit.T> items, string? model = null, GaOptions? o = null, IsoEvidence? ev = null)
    {
        var (sheets, _) = Pdf(items);
        var f = new GaChecker(ModelSnapshot.Parse(model ?? ModelJson()), "m", o, ev).Check(sheets);
        foreach (var x in f.Where(x => x.Status == FindingStatus.Fail))
        {
            Assert.False(string.IsNullOrWhiteSpace(x.DrawingValue), "FAIL without the drawing value: " + x.Message);
            Assert.False(string.IsNullOrWhiteSpace(x.SourceValue), "FAIL without the model value: " + x.Message);
            Assert.False(string.IsNullOrWhiteSpace(x.Source?.Document), "FAIL without the source: " + x.Message);
        }
        return f;
    }

    private static IEnumerable<Finding> Fails(IEnumerable<Finding> f) => f.Where(x => x.Status == FindingStatus.Fail);
    private static IEnumerable<Finding> Reviews(IEnumerable<Finding> f) => f.Where(x => x.Status == FindingStatus.Review);

    // ----- the model
    [Fact]
    public void Equipment_is_read_with_its_box_the_first_record_kept_and_a_nonsense_origin_ignored()
    {
        var snap = ModelSnapshot.Parse(ModelJson());
        Assert.True(snap.HasEquipment);
        Assert.Equal(3, snap.Equipment.Count);
        var pot = snap.FindEquipment("60-D-302").Single();     // any spelling of the tag
        Assert.Equal("60D302", pot.Tag);
        Assert.NotNull(pot.Origin);
        Assert.Null(snap.Equipment["/EARTHING-60U301"].Origin);          // POS is far outside the box
        Assert.Equal(0, pot.DistanceToBox((1577000, 3253000, 33000)));
        Assert.Equal(300, pot.DistanceToBox((1577784 + 300, 3253000, 33000)), 3);
        Assert.Empty(snap.FindEquipment("NOSUCH1"));
        Assert.False(ModelSnapshot.Parse(ModelJson(withEquipment: false)).HasEquipment);
    }

    [Fact]
    public void Tag_keys_ignore_hyphens_spaces_and_case_and_the_tag_is_the_part_before_the_description()
    {
        Assert.Equal("60D302", ModelSnapshot.TagKey("60-d-302"));
        Assert.Equal("60D302", ModelSnapshot.TagOf("/60D302_CONDENSATE_POT"));
        Assert.Equal("EARTHING-60U301", ModelSnapshot.TagOf("/EARTHING-60U301"));
    }

    // ----- the reader
    [Fact]
    public void Tags_stacked_imperial_coordinates_single_line_metric_coordinates_elevations_scale_revision_and_north_are_read()
    {
        var (sheets, _) = Pdf(Sheet(t => { t.Add(new(700, 400, "E 1576982 N 3253683 U 32224")); t.Add(new(700, 450, "EL 33.5 m")); t.Add(new(700, 480, "V-201")); return t; }));
        var s = Assert.Single(sheets);
        Assert.Equal(new[] { "60D302", "60E301", "V-201" }, s.Tags.Select(t => t.Text).Order().ToArray());
        Assert.Equal(2, s.Coords.Count);
        var stacked = s.Coords.Single(c => c.Imperial);
        Assert.InRange(stacked.E, 1576980.0, 1576984.0);        // printed to 1/16 inch (1.6 mm)
        var metric = s.Coords.Single(c => !c.Imperial);
        Assert.Equal((1576982.0, 3253683.0, 32224.0), (metric.E, metric.N, metric.U));
        Assert.Equal(new[] { 33500.0, 34164.0 }, s.Elevations.Select(e => e.Mm).Order().ToArray());
        Assert.Equal(("1:50", "0", true), (s.Scale, s.Revision, s.HasNorthArrow));
        Assert.Equal(1, s.Notes);
    }

    [Fact]
    public void Look_alike_text_that_is_not_shaped_like_a_tag_is_kept_for_the_typo_check_and_plain_words_are_not()
    {
        var (sheets, _) = Pdf(Sheet(t => { t.Add(new(700, 400, "60D3O2")); return t; }));
        Assert.Contains(sheets[0].Loose, l => l.Text == "60D3O2");
        Assert.DoesNotContain(sheets[0].Loose, l => l.Text == "SCALE");
    }

    [Fact]
    public void A_page_without_text_is_unreadable()
    {
        var (sheets, _) = Pdf([]);
        Assert.False(sheets[0].HasText);
        Assert.Contains(sheets[0].Warnings, w => w.Contains("no text layer"));
        var f = new GaChecker(ModelSnapshot.Parse(ModelJson()), "m").Check(sheets);
        Assert.Contains(Reviews(f), x => x.Message.Contains("no text"));
    }

    // ----- the check
    [Fact]
    public void A_correct_drawing_has_no_fail_and_no_review()
    {
        var f = Check(Sheet());
        Assert.Empty(Fails(f)); Assert.Empty(Reviews(f));
        foreach (var c in new[] { "equipment", "coordinates", "elevations", "drawing" }) Assert.Contains(f, x => x.Check == c && x.Status == FindingStatus.Ok);
        Assert.Contains(f, x => x.Check == "dimensions" && x.Status == FindingStatus.Info);
        Assert.Contains(f, x => x.Check == "clearances" && x.Status == FindingStatus.Info);
        Assert.Equal("OK", QcReport.Verdict(f));
    }

    [Fact]
    public void A_retyped_tag_is_a_fail_whether_or_not_it_still_looks_like_a_tag()
    {
        foreach (var typo in new[] { "60D3O2", "60D303", "60D32" })
        {
            var f = Check(Sheet(tag: typo));
            var x = Assert.Single(Fails(f), y => y.Check == "equipment");
            Assert.Equal(typo, x.DrawingValue); Assert.Equal("60D302", x.SourceValue);
            Assert.Contains("60D302_CONDENSATE_POT", x.Source!.Item);
        }
    }

    [Fact]
    public void A_tag_far_from_every_model_tag_is_a_review_not_a_fail()
    {
        var f = Check(Sheet(tag: "60X999"));
        Assert.Empty(Fails(f));
        Assert.Contains(Reviews(f), x => x.Check == "equipment" && x.DrawingValue == "60X999");
    }

    [Fact]
    public void A_coordinate_outside_the_equipment_extent_is_a_fail_with_the_distance_and_the_extent()
    {
        var x = Assert.Single(Fails(Check(Sheet(shiftMm: 1000))), y => y.Check == "coordinates");
        Assert.Contains("mm outside the equipment's extent", x.Message);
        Assert.Contains("E 1576180..1577784", x.SourceValue);
        Assert.Equal("60D302", x.Subject);
    }

    [Fact]
    public void A_coordinate_just_outside_the_box_is_ok_within_the_tolerance_and_a_fail_beyond_it()
    {
        Assert.Empty(Fails(Check(Sheet(shiftMm: 802 + 60))));                        // 60 mm outside the box (half width 802 mm), inside the 100 mm tolerance
        Assert.NotEmpty(Fails(Check(Sheet(shiftMm: 1200))));
        Assert.Empty(Fails(Check(Sheet(shiftMm: 1200), o: new GaOptions { BoxToleranceMm = 500 })));
    }

    [Fact]
    public void An_elevation_outside_the_height_range_is_a_review_because_what_it_refers_to_is_not_known()
    {
        var f = Check(Sheet(elevMm: 39164));
        Assert.Empty(Fails(f));
        Assert.Contains(Reviews(f), x => x.Check == "elevations" && x.DrawingValue.Contains("39164"));
    }

    [Fact]
    public void A_missing_scale_north_arrow_or_revision_is_a_review()
    {
        var f = Check(Sheet(t => t.Where(i => !i.Text.StartsWith("SCALE") && i.Text != "NORTH" && !i.Text.StartsWith("REV")).ToList()));
        Assert.Equal(3, Reviews(f).Count(x => x.Check == "drawing"));
    }

    [Fact]
    public void A_coordinate_label_that_is_not_next_to_a_tag_in_the_model_is_not_compared_and_says_so()
    {
        var f = Check(Sheet(t => { t.Add(new(900, 600, "E 100\"")); t.Add(new(900, 609, "N 100\"")); t.Add(new(900, 619, "U 100\"")); return t; }));
        Assert.Empty(Fails(f));
        Assert.Contains(f, x => x.Check == "coordinates" && x.Status == FindingStatus.Info && x.Message.Contains("not next to any equipment tag"));
    }

    [Fact]
    public void A_snapshot_from_before_equipment_was_exported_asks_for_a_new_export_instead_of_claiming_anything()
    {
        var f = Check(Sheet(), model: ModelJson(withEquipment: false));
        Assert.Contains(Reviews(f), x => x.Message.Contains("export the model again"));
        Assert.DoesNotContain(f, x => x.Check is "equipment" or "coordinates" && x.Status == FindingStatus.Ok);
    }

    // ----- P&ID and vendor data
    private IsoEvidence Evidence(bool pidHasTag, bool vendorHasTag)
    {
        var root = Path.Combine(_dir, "inputs-" + Guid.NewGuid().ToString("N"));
        foreach (var d in new[] { "pid", "vendor" }) Directory.CreateDirectory(Path.Combine(root, d));
        void Pdf(string path, string text) => Core.OfficeKit.PdfKit.Create(path, JsonSerializer.Serialize(new[] { new { type = "paragraph", text } }), true, "t");
        Pdf(Path.Combine(root, "pid", "P-1.pdf"), pidHasTag ? "P&ID. Condensate pot 60D302 and heater 60E301." : "P&ID. Other equipment 70X100.");
        Pdf(Path.Combine(root, "vendor", "V-1.pdf"), vendorHasTag ? "Vendor data sheet for 60D302 condensate pot. Weight 900 kg." : "Vendor data sheet for something else.");
        var lib = new InputsLibrary(root);
        var idx = new InputsIndex(lib, Path.Combine(_dir, "idx-" + Guid.NewGuid().ToString("N")));
        idx.Refresh();
        return new IsoEvidence(null, idx, lib.Inventory());
    }

    [Fact]
    public void Tags_found_in_the_pids_and_the_vendor_data_are_ok_and_tags_not_found_are_reviews_never_fails()
    {
        var ok = Check(Sheet(), ev: Evidence(true, true));
        Assert.Empty(Fails(ok));
        Assert.Contains(ok, x => x.Check == "tags" && x.Status == FindingStatus.Ok && x.Subject == "60D302");
        Assert.Contains(ok, x => x.Check == "vendor" && x.Status == FindingStatus.Ok && x.Subject == "60D302");
        var missing = Check(Sheet(), ev: Evidence(false, false));
        Assert.Empty(Fails(missing));
        Assert.Contains(Reviews(missing), x => x.Check == "tags" && x.Subject == "60D302");
        Assert.Contains(Reviews(missing), x => x.Check == "vendor" && x.Subject == "60D302");
    }

    [Fact]
    public void Without_pids_or_vendor_data_in_the_inputs_the_check_says_it_did_not_look()
    {
        var f = Check(Sheet(), ev: new IsoEvidence(null, null, []));
        Assert.Contains(f, x => x.Check == "tags" && x.Status == FindingStatus.Info && x.Message.Contains("no P&ID"));
        Assert.Contains(f, x => x.Check == "vendor" && x.Status == FindingStatus.Info && x.Message.Contains("no vendor data"));
    }
}

/// <summary>Runs only with PEC_QC_SAMPLES naming a folder with a real *.model.json that has equipment (client material: not in the repository).</summary>
public sealed class RealEquipmentFactAttribute : FactAttribute
{
    public RealEquipmentFactAttribute()
    {
        var d = Environment.GetEnvironmentVariable("PEC_QC_SAMPLES");
        if (string.IsNullOrWhiteSpace(d) || !Directory.Exists(d) || !Directory.GetFiles(d, "*.eq.model.json").Any())
            Skip = "set PEC_QC_SAMPLES to a folder with a real *.eq.model.json (a snapshot with equipment)";
    }
}

public class RealEquipmentTests
{
    [RealEquipmentFact]
    public void The_real_model_has_equipment_with_boxes_and_tags_that_resolve()
    {
        var snap = ModelSnapshot.Load(Directory.GetFiles(Environment.GetEnvironmentVariable("PEC_QC_SAMPLES")!, "*.eq.model.json")[0]);
        Assert.True(snap.HasEquipment);
        Assert.True(snap.Equipment.Count > 100);
        Assert.All(snap.Equipment.Values, e => Assert.True(e.HasBox));
        var pot = Assert.Single(snap.FindEquipment("60D302"));
        Assert.True(pot.Center is not null);
        Assert.Equal(0, pot.DistanceToBox(pot.Center!.Value));
    }
}
