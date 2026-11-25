using System.Text;
using Pec.Qc.Core.Findings;
using Pec.Qc.Core.Pid;

namespace Pec.Qc.Tests;

/// <summary>Writes small PDFs with text at exact places, including text rotated 90 degrees (line numbers run along pipes), without any drawing library. Positions are in the A3 reference frame from the top left.</summary>
public static class PidTestKit
{
    public sealed record T(double X, double Y, string Text, bool Rotated = false);

    public static void WritePdf(string path, IReadOnlyList<IReadOnlyList<T>> pages, double scale = 1.0)
    {
        double w = 1190.551 * scale, h = 841.889 * scale;
        var objs = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>" };
        var kids = new List<string>();
        foreach (var pg in pages)
        {
            var sb = new StringBuilder("BT\n/F1 8 Tf\n");
            foreach (var t in pg)
            {
                var x = t.X * scale; var y = h - t.Y * scale;
                var m = t.Rotated ? "0 1 -1 0" : "1 0 0 1";
                sb.Append($"{m} {x:0.##} {y:0.##} Tm ({t.Text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)")}) Tj\n");
            }
            sb.Append("ET");
            var content = sb.ToString();
            var contentNo = objs.Count + 2;   // 1-based number of the content object (the page object comes first)
            objs.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w:0.###} {h:0.###}] /Contents {contentNo} 0 R /Resources << /Font << /F1 3 0 R >> >> >>");
            kids.Add($"{objs.Count} 0 R");
            objs.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }
        objs[1] = $"<< /Type /Pages /Kids [{string.Join(" ", kids)}] /Count {kids.Count} >>";
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objs.Count; i++) { offsets.Add(pdf.Length); pdf.Append($"{i + 1} 0 obj\n{objs[i]}\nendobj\n"); }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objs.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) pdf.Append($"{o:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objs.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(pdf.ToString()));
    }

    /// <summary>A sheet with equipment, instrument bubbles, valves, line numbers (one rotated) and notes.</summary>
    public static List<T> Sheet(Func<List<T>, List<T>>? tweak = null)
    {
        var t = new List<T>
        {
            new(100, 100, "V-201"), new(100, 112, "DEHYDRATOR"), new(300, 100, "E-101"), new(500, 100, "P-101A"),
            new(300, 300, "XV-1001"), new(500, 300, "HV-1002"), new(700, 300, "XV-1001"),
            new(200, 200, "2\"-CD-60A07900-B400A-N-HS-017"), new(400, 450, "3\"-SC-60A05700-F241S-H-HS-002", true),
            new(600, 500, "PT"), new(600, 509, "1001"),
            new(800, 500, "NC"),
            new(100, 700, "NOTE 1: ALL VALVES TO BE LOCKED OPEN"), new(100, 712, "NOTE 2: SLOPE LINES TO DRAIN AT 1:100"),
        };
        return tweak is null ? t : tweak(t);
    }
}

public class PidTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pecqc-pid-" + Guid.NewGuid().ToString("N"));
    public PidTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private List<PidSheet> Read(string name, IReadOnlyList<IReadOnlyList<PidTestKit.T>> pages, double scale = 1.0)
    {
        var p = Path.Combine(_dir, name + ".pdf");
        PidTestKit.WritePdf(p, pages, scale);
        return PidReader.Read(p);
    }

    private IReadOnlyList<Finding> Compare(Func<List<PidTestKit.T>, List<PidTestKit.T>>? tweak, double scale = 1.0, PidOptions? o = null)
    {
        var orig = Read("original-" + Guid.NewGuid().ToString("N")[..6], [PidTestKit.Sheet()]);
        var red = Read("redrawn-" + Guid.NewGuid().ToString("N")[..6], [PidTestKit.Sheet(tweak)], scale);
        var f = new PidComparer(o).Compare(orig, red);
        foreach (var x in f.Where(x => x.Status == FindingStatus.Fail))
        {
            Assert.False(string.IsNullOrWhiteSpace(x.DrawingValue), "FAIL without the redrawn value: " + x.Message);
            Assert.False(string.IsNullOrWhiteSpace(x.SourceValue), "FAIL without the original value: " + x.Message);
            Assert.False(string.IsNullOrWhiteSpace(x.Source?.Document), "FAIL without the source: " + x.Message);
        }
        return f;
    }

    private static IEnumerable<Finding> Fails(IEnumerable<Finding> f) => f.Where(x => x.Status == FindingStatus.Fail);
    private static IEnumerable<Finding> Reviews(IEnumerable<Finding> f) => f.Where(x => x.Status == FindingStatus.Review);

    [Fact]
    public void Lines_rotated_lines_tags_bubbles_labels_and_notes_are_read_with_their_zone()
    {
        var s = Assert.Single(Read("a", [PidTestKit.Sheet()]));
        Assert.Contains(s.Items, i => i.Kind == PidKind.Line && i.Text == "2\"-CD-60A07900-B400A-N-HS-017" && !i.Vertical);
        var rotated = Assert.Single(s.Items, i => i.Kind == PidKind.Line && i.Vertical);
        Assert.Equal("3\"-SC-60A05700-F241S-H-HS-002", rotated.Text);
        Assert.Equal(new[] { "E-101", "HV-1002", "P-101A", "PT-1001", "V-201", "XV-1001", "XV-1001" }, s.Items.Where(i => i.Kind == PidKind.Tag).Select(i => i.Text).Order().ToArray());
        Assert.Contains(s.Items, i => i.Kind == PidKind.Label && i.Text == "DEHYDRATOR");
        Assert.Equal(2, s.Items.Count(i => i.Kind == PidKind.Note));
        Assert.Equal("A1", s.Items.Single(i => i.Text == "V-201").Zone);
    }

    [Fact]
    public void A_page_without_text_is_unreadable_not_empty()
    {
        var s = Assert.Single(Read("blank", [new List<PidTestKit.T>()]));
        Assert.False(s.HasText);
        Assert.Contains(s.Warnings, w => w.Contains("no text layer"));
    }

    [Fact]
    public void An_identical_copy_has_no_fail_and_no_review_and_still_says_the_graphics_were_not_checked()
    {
        var f = Compare(null);
        Assert.Empty(Fails(f)); Assert.Empty(Reviews(f));
        foreach (var check in new[] { "lines", "tags", "labels", "notes", "sheets" }) Assert.Contains(f, x => x.Check == check && x.Status == FindingStatus.Ok);
        Assert.Contains(f, x => x.Check == "graphics" && x.Status == FindingStatus.Info);
        Assert.Equal("OK", QcReport.Verdict(f));
    }

    [Fact]
    public void A_missing_tag_is_a_fail_with_the_original_text_and_where_it_was()
    {
        var x = Assert.Single(Fails(Compare(t => t.Where(i => i.Text != "E-101").ToList())));
        Assert.Equal(("tags", "(not on the redrawn drawing)", "E-101"), (x.Check, x.DrawingValue, x.SourceValue));
        Assert.Contains("zone", x.Source!.Page);
    }

    [Fact]
    public void A_missing_horizontal_and_a_missing_rotated_line_number_are_fails()
    {
        Assert.Contains(Fails(Compare(t => t.Where(i => !i.Text.StartsWith("2\"-CD")).ToList())), x => x.Check == "lines" && x.SourceValue.StartsWith("2\"-CD"));
        Assert.Contains(Fails(Compare(t => t.Where(i => !i.Rotated).ToList())), x => x.Check == "lines" && x.SourceValue.StartsWith("3\"-SC"));
    }

    [Fact]
    public void A_retyped_tag_is_one_changed_item_and_names_the_look_alike_character()
    {
        var f = Compare(t => t.Select(i => i.Text == "V-201" ? i with { Text = "V-2O1" } : i).ToList());
        var x = Assert.Single(Fails(f));
        Assert.Equal(("V-2O1", "V-201"), (x.DrawingValue, x.SourceValue));
        Assert.Contains("look-alike", x.Message);
        Assert.Empty(Reviews(f));   // not also reported as a missing tag plus an extra one
    }

    [Fact]
    public void A_changed_character_in_a_line_number_is_one_changed_item()
    {
        var f = Compare(t => t.Select(i => i.Text.StartsWith("2\"-CD") ? i with { Text = "2\"-CD-60A07900-B400A-N-HS-018" } : i).ToList());
        var x = Assert.Single(Fails(f));
        Assert.Equal(("2\"-CD-60A07900-B400A-N-HS-018", "2\"-CD-60A07900-B400A-N-HS-017"), (x.DrawingValue, x.SourceValue));
    }

    [Fact]
    public void A_tag_that_appears_twice_on_the_original_but_once_on_the_redrawn_drawing_is_a_fail()
    {
        var x = Assert.Single(Fails(Compare(t => { var i = t.FindLastIndex(a => a.Text == "XV-1001"); t.RemoveAt(i); return t; })));
        Assert.Equal("XV-1001", x.SourceValue);
    }

    [Fact]
    public void Something_only_on_the_redrawn_drawing_is_a_review_never_a_fail()
    {
        var f = Compare(t => { t.Add(new(900, 600, "FV-3001")); return t; });
        Assert.Empty(Fails(f));
        var r = Assert.Single(Reviews(f));
        Assert.Equal("FV-3001", r.DrawingValue);
    }

    [Fact]
    public void A_reworded_note_and_a_missing_note_are_fails_and_a_rewrapped_note_is_not()
    {
        var reworded = Compare(t => t.Select(i => i.Text.StartsWith("NOTE 1") ? i with { Text = "NOTE 1: ALL VALVES TO BE LOCKED CLOSED" } : i).ToList());
        var x = Assert.Single(Fails(reworded));
        Assert.Contains("LOCKED CLOSED", x.DrawingValue); Assert.Contains("LOCKED OPEN", x.SourceValue);
        Assert.Contains(Fails(Compare(t => t.Where(i => !i.Text.StartsWith("NOTE 2")).ToList())), y => y.Check == "notes" && y.SourceValue.StartsWith("NOTE 2"));
        var wrapped = Compare(t => t.SelectMany(i => i.Text.StartsWith("NOTE 2") ? new[] { new PidTestKit.T(i.X, i.Y, "NOTE 2: SLOPE LINES"), new PidTestKit.T(i.X, i.Y + 10, "TO DRAIN AT 1:100") } : [i]).ToList());
        Assert.Empty(Fails(wrapped));
        Assert.Contains(wrapped, y => y.Check == "notes" && y.Status == FindingStatus.Info && y.Message.Contains("line breaks differ"));
    }

    [Fact]
    public void An_incomplete_note_names_the_missing_words()
    {
        var f = Compare(t => t.Select(i => i.Text.StartsWith("NOTE 2") ? i with { Text = "NOTE 2: SLOPE LINES" } : i).ToList());
        Assert.Contains(Fails(f), x => x.Check == "notes" && (x.Message.Contains("DRAIN") || x.Message.Contains("worded differently")));
    }

    [Fact]
    public void A_moved_item_is_a_review_and_a_small_shift_or_a_different_page_size_is_not()
    {
        var moved = Compare(t => t.Select(i => i.Text == "P-101A" ? i with { X = i.X + 200 } : i).ToList());
        Assert.Empty(Fails(moved));
        Assert.Contains(Reviews(moved), x => x.Check == "position" && x.SourceValue.StartsWith("P-101A"));
        Assert.Empty(Reviews(Compare(t => t.Select(i => i.Text == "P-101A" ? i with { X = i.X + 10 } : i).ToList())));
        // the same drawing on a smaller sheet: positions are compared on the same frame
        var small = Compare(null, scale: 0.7);
        Assert.Empty(Fails(small)); Assert.Empty(Reviews(small));
    }

    [Fact]
    public void A_different_number_of_sheets_and_an_unreadable_sheet_are_reported()
    {
        var orig = Read("o2", [PidTestKit.Sheet(), PidTestKit.Sheet()]);
        var red = Read("r2", [PidTestKit.Sheet()]);
        var f = new PidComparer().Compare(orig, red);
        var x = Assert.Single(Fails(f), y => y.Check == "sheets");
        Assert.Equal(("1 sheets", "2 sheets"), (x.DrawingValue, x.SourceValue));
        var scan = new PidComparer().Compare(Read("o3", [PidTestKit.Sheet()]), Read("r3", [new List<PidTestKit.T>()]));
        Assert.Contains(Reviews(scan), y => y.Message.Contains("no text"));
        Assert.DoesNotContain(scan, y => y.Check is "tags" or "lines" && y.Status == FindingStatus.Ok);
    }

    [Fact]
    public void A_wider_move_tolerance_removes_the_review()
    {
        Func<List<PidTestKit.T>, List<PidTestKit.T>> shift = t => t.Select(i => i.Text == "P-101A" ? i with { X = i.X + 100 } : i).ToList();
        Assert.NotEmpty(Reviews(Compare(shift)));
        Assert.Empty(Reviews(Compare(shift, o: new PidOptions { MoveTolerance = 150 })));
    }

    [Fact]
    public void Only_pdf_files_are_read() => Assert.Throws<InvalidOperationException>(() => PidReader.Read(Path.Combine(_dir, "x.xyz")));
}
