using Pec.Qc.Core.OfficeKit;
using Pec.Qc.Core.Pid;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace Pec.Qc.Tests;

/// <summary>Each test reproduces something the QC team ran into when using the markup tools on real drawings.</summary>
public class PdfMarkupFieldTests
{
    static string Sheet(string dir, string name, double w, double h, params (string Text, double X, double Y)[] words)
    {
        var b = new PdfDocumentBuilder(); var page = b.AddPage(w, h);
        var font = b.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        foreach (var (t, x, y) in words) page.AddText(t, 8, new PdfPoint(x, y), font);
        var p = Path.Combine(dir, name); File.WriteAllBytes(p, b.Build()); return p;
    }

    [Fact] public void A_short_label_finds_only_that_label_never_the_end_of_one_word_and_the_start_of_the_next()
    {
        using var t = new TempDir();
        var src = Sheet(t.Path, "s.pdf", 1191, 842, ("DP", 100, 700), ("APD PRD", 300, 700), ("NOTE: AND PROVIDED", 500, 700), ("DP-101", 700, 700), ("SET", 100, 600), ("PRESSURE", 130, 600), ("PSV-512F", 400, 600));
        var hits = PdfMarkup.Find(src, "DP");
        Assert.Single(hits); Assert.True(hits[0].Box.X0 < 150);                                // the label only: not "APD PRD", not "AND PROVIDED", not the DP of the tag "DP-101"
        var part = PdfMarkup.Find(src, "DP", null, contains: true);
        Assert.Equal(2, part.Count);                                                           // part of a word, when asked for: the label and "DP-101"; a break between words is never crossed
        Assert.Single(PdfMarkup.Find(src, "512F", null, contains: true));                      // part of a word, when asked for
        Assert.Empty(PdfMarkup.Find(src, "512F"));                                              // ... and not by default
        Assert.Single(PdfMarkup.Find(src, "SET PRESSURE"));                                    // a phrase still works where the drawing has only a gap
        Assert.Single(PdfMarkup.Find(src, "psv-512f"));
        // the markup item follows the same rule and says so when nothing is found as a whole word
        var r = PdfMarkup.Apply(src, Path.Combine(t.Path, "o.pdf"), """[{"type":"highlight","find":"PD"},{"type":"highlight","find":"DP","all":true}]""", false);
        Assert.Equal(1, r.Highlights); Assert.Contains(r.Warnings, w => w.Contains("'PD'") && w.Contains("whole word"));        // "PD" is inside APD / PRD only: not found, and the warning says how to look
    }

    [Fact] public void Degree_squared_cubed_and_plus_minus_are_drawn_not_question_marks_and_what_the_font_cannot_show_is_reported()
    {
        using var t = new TempDir();
        var src = Sheet(t.Path, "s.pdf", 1191, 842, ("PSV-1", 300, 400));
        var o = Path.Combine(t.Path, "o.pdf");
        var r = PdfMarkup.Apply(src, o, "[{\"type\":\"cloud\",\"find\":\"PSV-1\",\"comment\":\"DESIGN 25 M\\u00B3 AT 90\\u00B0C \\u00B12 \\u2265 5 \\u2605\",\"color\":\"red\"}]", false);
        var raw = File.ReadAllText(o, System.Text.Encoding.Latin1);
        Assert.Contains("\\263", raw); Assert.Contains("\\260", raw); Assert.Contains("\\261", raw);       // cubed, degree, plus-minus as WinAnsi codes
        Assert.Contains("&gt;=", raw.Replace(">=", "&gt;=")); Assert.Contains(">=", raw);                  // the unequal sign was spelt out
        Assert.Contains(r.Warnings, w => w.Contains("U+2605"));                                           // the star cannot be drawn: said, not silent
        Assert.Equal(1, r.Warnings.Count(w => w.Contains("cannot show")));
    }

    [Fact] public void A_yellow_legend_line_is_black_on_a_yellow_band()
    {
        using var t = new TempDir();
        var src = Sheet(t.Path, "s.pdf", 1191, 842, ("A", 100, 100));
        var o = Path.Combine(t.Path, "o.pdf");
        PdfMarkup.Apply(src, o, """[{"type":"legend","lines":[{"text":"RED = CORRECTION","color":"red"},{"text":"YELLOW = UPDATED AS PER CLIENT","color":"yellow"}]}]""", false);
        var raw = File.ReadAllText(o, System.Text.Encoding.Latin1);
        var bt = raw.IndexOf("BT\n/F1", StringComparison.Ordinal);
        Assert.True(bt > 0);
        var before = raw[..bt]; var after = raw[bt..raw.IndexOf("ET\n", bt, StringComparison.Ordinal)];
        Assert.Contains("1 1 0 rg", before);                                                      // the band is drawn first
        Assert.DoesNotContain("1 1 0 rg", after);                                                 // the text is not yellow
        Assert.Contains("0 0 0 rg", after);
    }

    [Fact] public void Strikeout_is_a_real_annotation_and_a_comment_can_say_what_replaces_the_text()
    {
        using var t = new TempDir();
        var src = Sheet(t.Path, "s.pdf", 1191, 842, ("1/2\"", 300, 400), ("TAG-9", 600, 500));
        var o = Path.Combine(t.Path, "o.pdf");
        var r = PdfMarkup.Apply(src, o, """[{"type":"strikeout","find":"1/2\"","comment":"1-1/2\" AS PER RAW","color":"red"}]""", false);
        Assert.Empty(r.Warnings);
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(o); var a = pdf.GetPage(1).GetAnnotations().Select(x => x.Type.ToString()).ToList();
        Assert.Contains("StrikeOut", a); Assert.Contains("FreeText", a);                          // the line through the text and the box beside it
    }

    [Fact] public void A_box_measured_on_a_picture_needs_no_rect_the_whole_page_is_the_default_and_no_hand_conversion_is_needed()
    {
        using var t = new TempDir();
        var src = Sheet(t.Path, "s.pdf", 1191, 842, ("A", 100, 100));
        // a 72 dpi picture of the whole page is 1191 x 842 pixels: pixel (100,100)-(200,150) from the top left is the page box (100, 692)-(200, 742)
        var r = PdfMarkup.Apply(src, Path.Combine(t.Path, "o.pdf"), """[{"type":"cloud","px":[100,100,200,150],"in":{"dpi":72},"comment":"CHECK"},{"type":"cloud","px":[300,300,400,350],"comment":"AT 100 DPI"}]""", false);
        Assert.Equal(2, r.Clouds);
        Assert.Equal(new[] { 100.0, 692, 200, 742 }, r.Placed[0].Cloud!.Select(v => Math.Round(v)).ToArray());
        Assert.Equal(300 * 0.72, r.Placed[1].Cloud![0], 0);                                       // default dpi 100: 300 px = 216 pt
    }

    [Fact] public void A_comment_leader_never_crosses_another_comment_a_crowded_layout_is_either_clean_or_says_what_to_place_by_hand()
    {
        using var t = new TempDir();
        var words = Enumerable.Range(0, 14).Select(i => ($"TAG-{i:00}", 120.0 + (i % 7) * 140, 700.0 - (i / 7) * 60)).ToArray();
        var src = Sheet(t.Path, "s.pdf", 1191, 842, words);
        var items = "[" + string.Join(",", Enumerable.Range(0, 14).Select(i => $"{{\"type\":\"cloud\",\"find\":\"TAG-{i:00}\",\"comment\":\"UPDATE VALUE {i} AS PER THE NOTE ON THE RAW DRAWING SHEET\",\"color\":\"red\"}}")) + "]";
        var r = PdfMarkup.Apply(src, Path.Combine(t.Path, "o.pdf"), items, false);
        var segs = r.Placed.Select(p => { var box = new PdfMarkup.Box(p.Box[0], p.Box[1], p.Box[2], p.Box[3]); var c = new PdfMarkup.Box(p.Cloud![0], p.Cloud[1], p.Cloud[2], p.Cloud[3]); return (box, c, seg: PdfMarkup.Leader(box, c)); }).ToList();
        for (var i = 0; i < segs.Count; i++)
            for (var j = 0; j < segs.Count; j++)
            {
                if (i == j) continue;
                Assert.False(PdfMarkup.SegmentHitsBox(segs[i].seg.Fx, segs[i].seg.Fy, segs[i].seg.Tx, segs[i].seg.Ty, segs[j].box), $"the leader of comment {i} crosses the box of comment {j}");
                Assert.False(PdfMarkup.SegmentsCross(segs[i].seg.Fx, segs[i].seg.Fy, segs[i].seg.Tx, segs[i].seg.Ty, segs[j].seg.Fx, segs[j].seg.Fy, segs[j].seg.Tx, segs[j].seg.Ty), $"leaders {i} and {j} cross");
            }
        Assert.Equal(14, r.Placed.Count + r.Warnings.Count(w => w.Contains("NOT written")));   // every comment is placed or reported, none is lost silently
        Assert.All(segs, s => Assert.True(Math.Sqrt(Math.Pow(s.seg.Tx - s.seg.Fx, 2) + Math.Pow(s.seg.Ty - s.seg.Fy, 2)) <= 220 * 0.99 * 1.0 + 1, "no arrow across the sheet"));
    }

    [Fact] public void A_leader_never_runs_through_another_cloud_when_the_only_free_paper_is_on_one_side()
    {
        using var t = new TempDir();
        // a dense grid everywhere except the right hand strip: both comments can only go there, and the one for the left cloud would have to cross the right cloud
        var b = new PdfDocumentBuilder(); var page = b.AddPage(1191, 842); var font = b.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        for (var y = 60.0; y <= 780; y += 30) page.DrawLine(new PdfPoint(40, y), new PdfPoint(880, y), 1.0);
        for (var x = 40.0; x <= 880; x += 60) page.DrawLine(new PdfPoint(x, 60), new PdfPoint(x, 780), 1.0);
        page.AddText("TAG-A", 8, new PdfPoint(700, 405), font); page.AddText("TAG-B", 8, new PdfPoint(800, 405), font);
        var src = Path.Combine(t.Path, "s.pdf"); File.WriteAllBytes(src, b.Build());
        var r = PdfMarkup.Apply(src, Path.Combine(t.Path, "o.pdf"), """[{"type":"cloud","find":"TAG-A","comment":"UPDATE A","color":"red"},{"type":"cloud","find":"TAG-B","comment":"UPDATE B","color":"red"}]""", false);
        var clouds = r.Placed.Select(p => new PdfMarkup.Box(p.Cloud![0], p.Cloud[1], p.Cloud[2], p.Cloud[3])).ToList();
        for (var i = 0; i < r.Placed.Count; i++)
        {
            var box = new PdfMarkup.Box(r.Placed[i].Box[0], r.Placed[i].Box[1], r.Placed[i].Box[2], r.Placed[i].Box[3]); var seg = PdfMarkup.Leader(box, clouds[i]);
            for (var j = 0; j < clouds.Count; j++) if (i != j) Assert.False(PdfMarkup.SegmentHitsBox(seg.Fx, seg.Fy, seg.Tx, seg.Ty, clouds[j]), $"the leader of comment {i} runs through cloud {j}");
        }
        Assert.Equal(2, r.Clouds); Assert.Equal(2, r.Placed.Count + r.Warnings.Count(w => w.Contains("NOT written")));       // each comment is placed without a crossing, or reported with the way out ('at'): never lost silently
    }

    [Fact] public void The_leader_rule_refuses_a_box_whose_arrow_is_long_or_runs_through_a_comment_a_cloud_or_another_leader()
    {
        var cloud = new PdfMarkup.Box(100, 100, 140, 120);
        var none = Array.Empty<PdfMarkup.Box>(); var noLeaders = Array.Empty<(double, double, double, double)>();
        var box = new PdfMarkup.Box(200, 100, 350, 125);                                                       // to the right of the cloud: a short, straight arrow
        Assert.True(PdfMarkup.LeaderClear(box, cloud, none, none, noLeaders, 220));
        Assert.False(PdfMarkup.LeaderClear(new PdfMarkup.Box(500, 100, 650, 125), cloud, none, none, noLeaders, 220));        // too far: an arrow across the sheet
        Assert.False(PdfMarkup.LeaderClear(box, cloud, new[] { new PdfMarkup.Box(150, 95, 190, 130) }, none, noLeaders, 220)); // another comment in the way
        Assert.False(PdfMarkup.LeaderClear(box, cloud, none, new[] { new PdfMarkup.Box(150, 95, 190, 130) }, noLeaders, 220)); // another cloud in the way
        Assert.True(PdfMarkup.LeaderClear(box, cloud, none, new[] { cloud }, noLeaders, 220));                                  // its own cloud is not "another"
        Assert.False(PdfMarkup.LeaderClear(box, cloud, none, none, new[] { (170.0, 50.0, 170.0, 200.0) }, 220));              // another arrow crosses it
        Assert.True(PdfMarkup.LeaderClear(box, cloud, none, none, new[] { (170.0, 150.0, 170.0, 300.0) }, 220));              // an arrow that does not
    }

    [Fact] public void A_markup_file_written_by_the_tool_is_recognised_so_it_can_be_regenerated_and_other_files_are_not()
    {
        using var t = new TempDir();
        var src = Sheet(t.Path, "s.pdf", 1191, 842, ("A", 100, 100));
        var o = Path.Combine(t.Path, "o.pdf"); PdfMarkup.Apply(src, o, """[{"type":"highlight","rect":[90,95,120,115]}]""", false);
        Assert.True(PdfMarkup.IsOwnMarkup(o)); Assert.False(PdfMarkup.IsOwnMarkup(src)); Assert.False(PdfMarkup.IsOwnMarkup(Path.Combine(t.Path, "missing.pdf")));
        // the marker survives a second round
        var o2 = Path.Combine(t.Path, "o2.pdf"); PdfMarkup.Apply(o, o2, """[{"type":"highlight","rect":[200,200,230,220]}]""", false); Assert.True(PdfMarkup.IsOwnMarkup(o2));
    }

    [Fact] public void Two_books_with_no_tags_in_common_are_flagged_as_not_a_pair_before_any_finding_is_read()
    {
        PidSheet Book(params string[] texts) { var s = new PidSheet(); var k = 0; foreach (var x in texts) s.Items.Add(new PidItem(PidKind.Tag, x, 10 + k++ * 20, 10, false)); return s; }
        var a = new[] { Book("V-101", "V-102", "P-101A", "P-101B", "PT-1001", "PT-1002", "XV-1001", "XV-1002", "TK-A", "TK-B") };
        var same = PidPairCheck.Check(a, a); Assert.Null(same.Warning); Assert.Equal(100, same.OverlapPercent);
        var other = new[] { Book("021-FD-0005", "021-VG-0002", "021-PX-0001", "021-PX-0003", "FV-3001", "FV-3002", "LT-4001", "LT-4002", "E-9001", "E-9002") };
        var diff = PidPairCheck.Check(a, other); Assert.Contains("true pair", diff.Warning); Assert.Equal(0, diff.OverlapPercent);
        var empty = PidPairCheck.Check(a, new[] { new PidSheet() }); Assert.Contains("almost no text", empty.Warning);
    }
}
