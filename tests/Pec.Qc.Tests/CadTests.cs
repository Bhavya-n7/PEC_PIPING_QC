using Pec.Qc.Core.Cad;

namespace Pec.Qc.Tests;

/// <summary>DXF reading (a drawing made by a CAD library with known content) and DWG conversion through AutoCAD's console when AutoCAD is installed.</summary>
[Collection("EnvVar")]
public class CadTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "pecqc-cad-" + Guid.NewGuid().ToString("N"));
    public CadTests() { Directory.CreateDirectory(_tmp); }
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    private static string Data(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "tests", "Pec.Qc.Tests", "data", name);
            if (File.Exists(f)) return f;
        }
        throw new FileNotFoundException(name);
    }

    [Fact]
    public void A_dxf_is_read_into_texts_attributes_dimensions_layers_units_and_line_lengths()
    {
        var d = DxfReader.Read(Data("sample-ezdxf.dxf"));
        Assert.Equal("AC1032", d.Version); Assert.Equal("mm", d.Units);
        Assert.Contains("PIPE", d.Layers); Assert.Contains("TAGS", d.Layers); Assert.Contains("GATEVALVE", d.BlockDefinitions);
        Assert.Equal(2, d.EntityCounts["LINE"]);
        Assert.Equal(6000, Math.Round(d.LineLengthByLayer["PIPE"]), 1);                     // 5000 (3-4-5) + 1000
        Assert.Contains(d.Texts, t => t.Kind == "TEXT" && t.Text == "6\"-GH5100-28-GAS-4297-A1A" && t.Layer == "TAGS");
        var note = d.Texts.Single(t => t.Kind == "MTEXT");
        Assert.Equal("NOTE 1:\nALL WELDS 100% RT\ndesign Ø150 °", note.Text);               // formatting codes and symbols resolved
        var valve = Assert.Single(d.Inserts);
        Assert.Equal("GATEVALVE", valve.Block); Assert.Equal("XV-1001", valve.Attributes["TAG"]); Assert.Equal("GATE 6in 150#", valve.Attributes["DESC"]);
        Assert.Contains(d.Texts, t => t.Kind == "DIMENSION" && t.Text == "5000");
        Assert.Equal(0, d.Extents[0]); Assert.True(d.Extents[2] >= 3000);
    }

    [Fact]
    public void Text_cleaning_handles_unicode_stacked_text_and_fonts()
    {
        Assert.Equal("Ø25 ±1 °", DxfReader.Clean("%%c25 %%p1 %%d"));
        Assert.Equal("1/2\" NPS", DxfReader.Clean("\\S1^2;\" NPS"));
        Assert.Equal("é", DxfReader.Clean("\\U+00E9"));
        Assert.Equal("BOLD text", DxfReader.Clean("{\\fArial|b1;BOLD} text"));
    }

    [Fact]
    public void Things_that_are_not_an_ascii_dxf_are_refused_with_a_reason()
    {
        var bin = Path.Combine(_tmp, "b.dxf"); File.WriteAllBytes(bin, System.Text.Encoding.ASCII.GetBytes("AutoCAD Binary DXF\r\n\u001a\0rest"));
        Assert.Contains("binary DXF", Assert.Throws<InvalidDataException>(() => DxfReader.Read(bin)).Message);
        var junk = Path.Combine(_tmp, "j.dxf"); File.WriteAllText(junk, "hello world\nthis is not a drawing\n");
        Assert.Throws<InvalidDataException>(() => DxfReader.Read(junk));
    }

    [Fact]
    public void A_truncated_dxf_still_gives_what_was_read_and_does_not_crash()
    {
        var all = File.ReadAllText(Data("sample-ezdxf.dxf"));
        var cut = Path.Combine(_tmp, "cut.dxf"); File.WriteAllText(cut, all[..(all.IndexOf("INSERT", all.IndexOf("ENTITIES", StringComparison.Ordinal), StringComparison.Ordinal) + 3)]);
        var d = DxfReader.Read(cut);
        Assert.Contains(d.Texts, t => t.Kind == "TEXT");
    }

    [Fact]
    public void A_dwg_is_converted_by_autocad_and_read_and_the_original_is_untouched()
    {
        if (DwgConverter.FindConsole() is null) { return; }                                   // no AutoCAD on this PC: nothing to test here
        var dwg = Path.Combine(_tmp, "drawing.dwg"); File.Copy(Data("sample.dwg"), dwg);
        var before = File.GetLastWriteTimeUtc(dwg); var size = new FileInfo(dwg).Length;
        var dxf = DwgConverter.ToDxf(dwg, Path.Combine(_tmp, "out"));
        var d = DxfReader.Read(dxf);
        Assert.Contains(d.Texts, t => t.Text == "6\"-GH5100-28-GAS-4297-A1A");
        Assert.Equal("XV-1001", Assert.Single(d.Inserts).Attributes["TAG"]);
        Assert.Equal(before, File.GetLastWriteTimeUtc(dwg)); Assert.Equal(size, new FileInfo(dwg).Length);
    }

    [Fact]
    public void Without_autocad_the_message_says_what_to_do_instead()
    {
        Environment.SetEnvironmentVariable("PEC_QC_ACCORE", null);
        var saved = DwgConverter.FindConsole();
        if (saved is not null) return;                                                        // AutoCAD present: the message cannot be seen here
        var ex = Assert.Throws<InvalidOperationException>(() => DwgConverter.ToDxf(Data("sample.dwg"), _tmp));
        Assert.Contains("save it as DXF or PDF", ex.Message);
    }
}

/// <summary>The P&amp;ID comparison and the GA reading work on DXF drawings through the same readers as for PDFs.</summary>
public class CadSheetTests
{
    private static string Data(string name)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "tests", "Pec.Qc.Tests", "data", name);
            if (File.Exists(f)) return f;
        }
        throw new FileNotFoundException(name);
    }

    [Fact]
    public void A_dxf_pid_is_read_into_lines_tags_notes_with_zones_and_a_rotated_line_is_vertical()
    {
        var s = Assert.Single(Pec.Qc.Core.Pid.PidReader.Read(Data("pid-original.dxf")));
        Assert.Contains(s.Items, i => i.Kind == Pec.Qc.Core.Pid.PidKind.Line && i.Text == "6\"-GH-5100-28-GAS-A1A");
        Assert.Contains(s.Items, i => i.Kind == Pec.Qc.Core.Pid.PidKind.Line && i.Text == "4\"-PW-220-04-WTR-A1A" && i.Vertical);
        Assert.Equal(new[] { "E-101", "P-301A", "V-201" }, s.Items.Where(i => i.Kind == Pec.Qc.Core.Pid.PidKind.Tag).Select(i => i.Text).Order().ToArray());
        Assert.Contains(s.Items, i => i.Kind == Pec.Qc.Core.Pid.PidKind.Note && i.Text.Contains("FULL BORE"));
    }

    [Fact]
    public void A_redrawn_dxf_pid_is_compared_with_the_original_a_missing_tag_and_a_changed_line_are_FAIL_and_an_extra_tag_is_REVIEW()
    {
        var orig = Pec.Qc.Core.Pid.PidReader.Read(Data("pid-original.dxf")); var red = Pec.Qc.Core.Pid.PidReader.Read(Data("pid-redrawn.dxf"));
        var f = new Pec.Qc.Core.Pid.PidComparer(new Pec.Qc.Core.Pid.PidOptions()).Compare(orig, red);
        var fails = f.Where(x => x.Status == Pec.Qc.Core.Findings.FindingStatus.Fail).ToList();
        Assert.Contains(fails, x => x.SourceValue.Contains("E-101"));
        Assert.Contains(fails, x => x.SourceValue.Contains("CD-101") && x.DrawingValue.Contains("CD-102"));
        Assert.DoesNotContain(fails, x => x.SourceValue.Contains("V-201") || x.SourceValue.Contains("P-301A") || x.SourceValue.Contains("FULL BORE"));
        Assert.Contains(f, x => x.Status == Pec.Qc.Core.Findings.FindingStatus.Review && (x.DrawingValue.Contains("X-999") || x.Message.Contains("X-999")));
        // the same drawing against itself: nothing fails
        Assert.DoesNotContain(new Pec.Qc.Core.Pid.PidComparer(new Pec.Qc.Core.Pid.PidOptions()).Compare(orig, Pec.Qc.Core.Pid.PidReader.Read(Data("pid-original.dxf"))), x => x.Status == Pec.Qc.Core.Findings.FindingStatus.Fail);
    }

    [Fact]
    public void A_dxf_ga_gives_equipment_tags_coordinates_elevation_scale_and_revision()
    {
        var s = Assert.Single(Pec.Qc.Core.Ga.GaReader.Read(Data("ga-sample.dxf")));
        Assert.Equal(new[] { "60D302", "60E301" }, s.Tags.Select(t => t.Text).Order().ToArray());
        Assert.Equal(2, s.Coords.Count); Assert.Contains(s.Coords, c => c.E == 1576982 && c.N == 3253683 && c.U == 32224);
        Assert.Contains("1:50", s.Scale ?? ""); Assert.Equal("0", s.Revision);
    }
}
