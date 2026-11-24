using System.Text.RegularExpressions;
using Pec.Qc.Core.Model;
using UglyToad.PdfPig;

namespace Pec.Qc.Core.Iso;

/// <summary>
/// Reads a piping isometric (spool drawing) PDF with a text layer: title block, BOM, cut pipe lengths, coordinate labels and sheet links, one <see cref="IsoSheet"/> per page.
/// It reads text and positions only. The layout it expects is the one of the sample isometrics (title block bottom centre, BOM at the right, cut table under it); a
/// sheet it cannot make sense of produces warnings instead of guesses, and a drawing without a text layer (a scan) is reported as unreadable.
/// </summary>
public static partial class IsoReader
{
    [GeneratedRegex("""\d+(?:\s?\d/\d)?"-[A-Z0-9]{1,4}-[A-Z0-9]+-[A-Z0-9]+-[A-Z0-9]+-[A-Z0-9]+-\d+""")]
    public static partial Regex LineNoRegex();

    [GeneratedRegex("""^([ENU])\s+(-?\d+(?:\s+\d+/\d+)?)\s*"?$""")]
    private static partial Regex CoordRegex();

    private static readonly HashSet<string> Categories = new(StringComparer.Ordinal)
        { "PIPE", "FITTINGS", "FLANGES", "GASKETS", "BOLTS", "SUPPORTS", "VALVES", "INSTRUMENTS", "SPECIALTY ITEMS", "MISC", "OTHERS" };

    /// <summary>The title-block labels as printed on the isometric.</summary>
    public static readonly string[] ParamLabels =
    [
        "TEST PRESS. (psig)", "TEST MEDIUM", "DESIGN TEMP. (deg F)", "DESIGN PRESS. (psig)", "OPER TEMP. (deg F)", "OPER PRESS. (psig)", "PAINT SYSTEM", "INSU. TYPE",
        "INSU. THK", "PHASE", "P&ID NO", "STRESS CRITICALITY", "PIPNG CLASS", "PWHT",
    ];

    public static List<IsoSheet> Read(string pdfPath)
    {
        if (!string.Equals(Path.GetExtension(pdfPath), ".pdf", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Only .pdf files are read.");
        using var doc = PdfDocument.Open(pdfPath);
        var sheets = new List<IsoSheet>();
        for (var i = 1; i <= doc.NumberOfPages; i++) sheets.Add(ReadSheet(PdfTextLines.Read(doc.GetPage(i)), pdfPath, i));
        return sheets;
    }

    public static IsoSheet ReadSheet(List<TextLine> L, string file, int pageNo)
    {
        var sh = new IsoSheet { File = Path.GetFileName(file), Page = pageNo };
        if (L.Count == 0) { sh.Warnings.Add("the page has no text layer (a scan or an image): it cannot be read"); return sh; }
        // the line number printed in the title block (bottom right)
        var cands = L.Where(r => LineNoRegex().Match(r.T) is { Success: true } m && m.Value == r.T && r.Y0 > 700 && r.X0 > 900).ToList();
        if (cands.Count > 0) sh.LineNo = cands[0].T; else sh.Warnings.Add("title-block line number not found");
        // sheet x/n and revision, bottom right corner
        var corner = string.Concat(L.Where(r => r.Y0 > 795 && r.X0 > 1100 && r.X0 < 1150).Select(r => r.T.Replace(" ", "")));
        var cm = Regex.Match(corner, @"(\d+)/(\d+)");
        if (cm.Success) { sh.Sheet = int.Parse(cm.Groups[1].Value); sh.Sheets = int.Parse(cm.Groups[2].Value); }
        var revs = L.Where(r => Regex.IsMatch(r.T, @"^\d{2}$") && r.X0 is > 955 and < 985 && r.Y0 > 600).Select(r => r.T).ToList();
        sh.Revision = revs.Count > 0 ? revs.MaxBy(int.Parse)! : "";
        ParseLinks(L, sh);
        ParseParams(L, sh);
        sh.Bom.AddRange(ParseBom(L, sh.Warnings));
        sh.Cuts.AddRange(ParseCuts(L));
        sh.Coords.AddRange(ParseCoords(L));
        if (sh.Bom.Count == 0) sh.Warnings.Add("no BOM rows found");
        return sh;
    }

    // ----------------------------------------------------------------------------- sheet links
    private static void ParseLinks(List<TextLine> L, IsoSheet sh)
    {
        foreach (var r in L)
        {
            var one = Regex.Match(r.T, @"CONT\.\s*(ON|FROM)\s+DRG\s*(\d+)");
            if (one.Success) { sh.Links.Add(new(one.Groups[1].Value, int.Parse(one.Groups[2].Value))); continue; }
            var head = Regex.Match(r.T, @"^CONT\.\s*(ON|FROM)$");
            if (!head.Success) continue;
            // "DRG 2" is the next line, same left edge
            var drg = L.FirstOrDefault(s => Math.Abs(s.X0 - r.X0) < 6 && s.Y0 > r.Y0 && s.Y0 - r.Y0 < 16 && Regex.IsMatch(s.T, @"^DRG\s*\d+$"));
            if (drg is not null) sh.Links.Add(new(head.Groups[1].Value, int.Parse(Regex.Match(drg.T, @"\d+").Value)));
        }
    }

    // ----------------------------------------------------------------------------- title block parameters
    private static void ParseParams(List<TextLine> L, IsoSheet sh)
    {
        foreach (var label in ParamLabels)
        {
            var lab = L.FirstOrDefault(r => r.T == label && r.Y0 > 600);
            if (lab is null) continue;
            var cands = L.Where(r => Math.Abs(r.Y0 - lab.Y0) < 3 && r.X0 > lab.X1 && r.X0 < lab.X1 + 120 && r.T != ":").OrderBy(r => r.X0).ToList();
            var vals = new List<string>();
            foreach (var r in cands)
            {
                if (r.T.StartsWith("CALC NO", StringComparison.Ordinal)) break;
                vals.Add(r.T.TrimStart(':', ' ').Trim());
            }
            sh.Params[label] = vals.Count > 0 ? vals[0] : "";
        }
        var calc = L.FirstOrDefault(r => r.T.StartsWith("CALC NO", StringComparison.Ordinal) && r.Y0 > 600);
        if (calc is not null)
        {
            // "CALC NO : NA" in one line, or "CALC NO :" with the value as the next line to the right
            var rest = calc.T["CALC NO".Length..].Trim().TrimStart(':').Trim();
            if (rest.Length > 0) sh.Params["CALC NO"] = rest;
            else
            {
                var v = L.Where(r => Math.Abs(r.Y0 - calc.Y0) < 3 && r.X0 > calc.X1 - 1).OrderBy(r => r.X0).FirstOrDefault();
                sh.Params["CALC NO"] = v?.T ?? "";
            }
        }
    }

    // ----------------------------------------------------------------------------- BOM
    private sealed class Cur(string section, string category, string pt, double y)
    {
        public string Section = section, Category = category, Pt = pt; public string Desc = "", Size = "", QtyText = ""; public double Y = y;
        public BomRow ToRow() => new(Section, Category, Pt, Desc, Size, QtyText, null);
    }

    private static List<BomRow> ParseBom(List<TextLine> L, List<string> warnings)
    {
        var rows = new List<BomRow>();
        var heads = L.Where(r => r.T is "FABRICATION MATERIALS" or "ERECTION MATERIALS").ToList();
        var cutHdr = L.FirstOrDefault(r => r.T == "CUT PIPE LENGTH");
        foreach (var h in heads)
        {
            var end = double.PositiveInfinity;
            var later = heads.Concat(cutHdr is null ? [] : [cutHdr]).Where(r => r.Y0 > h.Y0 + 1 && Math.Abs(r.X0 - h.X0) < 60).Select(r => r.Y0).ToList();
            if (later.Count > 0) end = later.Min();
            var region = L.Where(r => r.Y0 > h.Y0 && r.Y0 < end && r.X0 >= h.X0 - 5).ToList();
            var pt = region.FirstOrDefault(r => r.T == "PT");
            var desc = region.FirstOrDefault(r => r.T == "COMPONENT DESCRIPTION");
            var ns = region.FirstOrDefault(r => r.T == "N.S.");
            var qty = region.FirstOrDefault(r => r.T == "QTY");
            if (pt is null || desc is null || ns is null || qty is null) { warnings.Add($"BOM header columns not found under {h.T}"); continue; }
            double xDesc = desc.X0 - 4, xNs = ns.X0 - 6, xQty = qty.X0 - 6;
            var body = region.Where(r => r.Y0 > Math.Max(pt.Y1, ns.Y1) + 12 && r.T is not ("(INS)" or "NO")).ToList();
            var section = h.T.StartsWith("FAB", StringComparison.Ordinal) ? "FABRICATION" : "ERECTION";
            var cat = "";
            Cur? cur = null;
            var done = new List<Cur>();
            foreach (var r in body)
            {
                var t = r.T;
                if (r.X0 < xDesc && Regex.IsMatch(t, @"^\d+$"))
                {
                    if (cur is not null) done.Add(cur);
                    cur = new Cur(section, cat, t, r.Y0);
                    continue;
                }
                if (r.X0 >= xDesc && r.X0 < xNs && Categories.Contains(t.ToUpperInvariant()) && r.X1 - r.X0 < 120)
                {
                    if (cur is not null) { done.Add(cur); cur = null; }
                    cat = t.ToUpperInvariant();
                    continue;
                }
                if (cur is null) continue;
                var sameRow = Math.Abs(r.Y0 - cur.Y) < 3;
                if (r.X0 >= xDesc && r.X0 < xNs)
                {
                    if (sameRow && r.X1 > xNs && cur.Size.Length == 0)
                    {
                        // the description runs into the N.S. column: "... RF 2"
                        var m = Regex.Match(t, @"^(.*\S)\s+(\d+(?:\s?[xX]\s?\d+)*)$");
                        if (m.Success) { t = m.Groups[1].Value; cur.Size = m.Groups[2].Value; }
                    }
                    cur.Desc = (cur.Desc + " " + t).Trim();
                }
                else if (r.X0 >= xNs && r.X0 < xQty && sameRow) cur.Size = t;
                else if (r.X0 >= xQty && sameRow) cur.QtyText = t;
            }
            if (cur is not null) done.Add(cur);
            foreach (var d in done)
            {
                var row = d.ToRow();
                var q = row.Category == "PIPE" ? Units.FeetInchesToInches(row.QtyText) : Regex.IsMatch(row.QtyText.Trim(), @"^\d+$") ? double.Parse(row.QtyText) : (double?)null;
                if (q is null) warnings.Add($"BOM item {row.PtNo}: quantity '{row.QtyText}' not understood");
                rows.Add(row with { Qty = q });
            }
        }
        return rows;
    }

    // ----------------------------------------------------------------------------- cut pipe lengths
    private static List<CutPiece> ParseCuts(List<TextLine> L)
    {
        var hdr = L.FirstOrDefault(r => r.T == "CUT PIPE LENGTH");
        var cuts = new List<CutPiece>();
        if (hdr is null) return cuts;
        foreach (var r in L)
        {
            if (!(r.Y0 > hdr.Y0 + 20 && Math.Abs(r.X0 - (hdr.X0 - 25)) < 14 && Regex.IsMatch(r.T, @"^<\d+>$"))) continue;
            var same = L.Where(s => Math.Abs(s.Y0 - r.Y0) < 3 && s.X0 > r.X0 + 5 && s.X0 < hdr.X0 + 200).ToList();
            var length = same.FirstOrDefault(s => s.X0 < hdr.X0 + 50);
            var size = same.FirstOrDefault(s => s.X0 >= hdr.X0 + 50 && s.X0 < hdr.X0 + 100);
            var lt = length?.T ?? "";
            cuts.Add(new CutPiece(r.T, lt, Units.FeetInchesToInches(lt), size?.T ?? ""));
        }
        return cuts;
    }

    // ----------------------------------------------------------------------------- coordinate labels
    /// <summary>E / N / U labels are three stacked text lines. They are grouped by position (same left edge, consecutive lines).</summary>
    private static List<CoordLabel> ParseCoords(List<TextLine> L)
    {
        var coord = new List<(char Axis, double V, TextLine R)>();
        foreach (var r in L)
        {
            var m = CoordRegex().Match(r.T);
            if (m.Success && Units.FeetInchesToInches(m.Groups[2].Value) is { } v) coord.Add((m.Groups[1].Value[0], v, r));
        }
        var outl = new List<CoordLabel>();
        var used = new HashSet<int>();
        for (var i = 0; i < coord.Count; i++)
        {
            var (ax, v, r) = coord[i];
            if (ax != 'E' || used.Contains(i)) continue;
            var group = new Dictionary<char, double> { ['E'] = v };
            var last = r;
            foreach (var want in new[] { 'N', 'U' })
            {
                var nxt = -1;
                for (var j = 0; j < coord.Count; j++)
                {
                    var (a2, _, r2) = coord[j];
                    if (!used.Contains(j) && a2 == want && Math.Abs(r2.X0 - r.X0) < 4 && r2.Y0 - last.Y0 is > 4 and < 18) { nxt = j; break; }
                }
                if (nxt < 0) break;
                group[want] = coord[nxt].V; used.Add(nxt); last = coord[nxt].R;
            }
            if (group.Count != 3) continue;
            // the CONT. ON/FROM caption and the line number sit in the lines just above the E line, same left edge
            var above = L.Where(s => Math.Abs(s.X0 - r.X0) < 6 && s.Y0 > r.Y0 - 48 && s.Y0 < r.Y0 - 1).Select(s => s.T).ToList();
            var joined = string.Join(" ", above.Append(r.T));
            var cont = joined.Contains("CONT. ON", StringComparison.Ordinal) ? "ON" : joined.Contains("CONT. FROM", StringComparison.Ordinal) ? "FROM" : "";
            var lm = LineNoRegex().Match(joined);
            outl.Add(new CoordLabel(group['E'], group['N'], group['U'], cont, lm.Success ? lm.Value : "", joined));
        }
        return outl;
    }
}
