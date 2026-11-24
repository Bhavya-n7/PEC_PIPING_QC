using System.Text.RegularExpressions;
using Pec.Qc.Core.Model;
using Pec.Qc.Core.Pid;
using UglyToad.PdfPig;

namespace Pec.Qc.Core.Ga;

public sealed record GaTag(string Text, string Key, double X, double Y);

/// <summary>A coordinate printed on the drawing, in mm. Imperial = it was written in feet and inches; otherwise the value was a plain number, taken as mm (AssumedMm).</summary>
public sealed record GaCoord(double E, double N, double U, double X, double Y, bool Imperial, string Raw);

public sealed record GaElevation(double Mm, double X, double Y, bool Imperial, string Raw);

/// <summary>What was read from one sheet of a general arrangement PDF.</summary>
public sealed class GaSheet
{
    public string File { get; set; } = "";
    public int Page { get; set; }
    public List<GaTag> Tags { get; } = [];
    /// <summary>Tokens that have letters and digits but do not look like a tag (for example 60D3O2): the check reports those that are one character from a model tag.</summary>
    public List<GaTag> Loose { get; } = [];
    public List<GaCoord> Coords { get; } = [];
    public List<GaElevation> Elevations { get; } = [];
    public string Scale { get; set; } = "";
    public string Revision { get; set; } = "";
    public bool HasNorthArrow { get; set; }
    public int Notes { get; set; }
    public List<string> Warnings { get; } = [];
    public bool HasText { get; set; }
}

/// <summary>
/// Reads a general arrangement (GA) drawing PDF's text layer: equipment tags, E/N/U coordinate labels, elevations, scale, revision, north arrow and the number of notes.
/// It reads text and positions only. Dimensions are not interpreted (what a number is the dimension of is a drawing, not text) and symbols are not read. Tuned on
/// synthetic drawings: a real client GA template may need the patterns here adjusted.
/// </summary>
public static partial class GaReader
{
    // 60D302  60-D-302  60PT3025  V-201  P-101A  (a plant code in front, or letters and a hyphen: plain words with digits such as REV01 or A1 are not tags)
    [GeneratedRegex(@"(?<![A-Za-z0-9\-])(?:\d{2,3}-?[A-Z]{1,4}-?\d{2,5}[A-Z]{0,2}|[A-Z]{1,3}-\d{3,5}[A-Z]{0,2})(?![A-Za-z0-9\-])")]
    private static partial Regex TagRx();

    [GeneratedRegex(@"(?<![A-Za-z0-9\-])[A-Z0-9][A-Z0-9\-]{4,}(?![A-Za-z0-9\-])")]
    private static partial Regex LooseRx();

    [GeneratedRegex(@"^([ENU])\s*[:=]?\s*(.+)$")]
    private static partial Regex AxisRx();

    [GeneratedRegex(@"\bEL(?:EV(?:ATION)?)?\.?\s*[:=]?\s*([+\-]?\s*\d[\d.,]*(?:\s?\d/\d)?|[+\-]?\s*\d+\s*'[^A-Za-z]*)\s*(mm|m)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex ElevRx();

    [GeneratedRegex(@"SCALE\s*[:=]?\s*(1\s*:\s*\d+|NTS|AS SHOWN)", RegexOptions.IgnoreCase)]
    private static partial Regex ScaleRx();

    [GeneratedRegex(@"\bREV(?:ISION)?\.?\s*[:=\-]?\s*([A-Z0-9]{1,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex RevRx();

    public static List<GaSheet> Read(string pdfPath)
    {
        if (!string.Equals(Path.GetExtension(pdfPath), ".pdf", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Only .pdf files are read.");
        using var doc = PdfDocument.Open(pdfPath);
        var sheets = new List<GaSheet>();
        for (var i = 1; i <= doc.NumberOfPages; i++) sheets.Add(ReadLines(PidReader.TextLines(doc.GetPage(i)), pdfPath, i));
        return sheets;
    }

    private static double? Number(string v, out bool imperial)
    {
        imperial = false;
        var t = v.Trim();
        if (t.Contains('\'') || t.Contains('"') || Regex.IsMatch(t, @"\d\s+\d+/\d+"))
        {
            imperial = true;
            return Units.FeetInchesToInches(t) is { } inch ? Units.InToMm(inch) : null;
        }
        var m = Regex.Match(t, @"^([+\-]?\d+(?:\.\d+)?)\s*(mm|m)?$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var n = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return m.Groups[2].Value.Equals("m", StringComparison.OrdinalIgnoreCase) ? n * 1000 : n;
    }

    public static GaSheet ReadLines(List<PidReader.RawLine> lines, string file, int pageNo)
    {
        var sh = new GaSheet { File = Path.GetFileName(file), Page = pageNo, HasText = lines.Count > 0 };
        if (lines.Count == 0) { sh.Warnings.Add("the page has no text layer (a scan or an image): it cannot be read"); return sh; }
        var horizontal = lines.Where(l => !l.Vertical).OrderBy(l => l.Y0).ThenBy(l => l.X0).ToList();
        // coordinates: a line holding E, N and U, or three stacked lines (same left edge, consecutive)
        var usedAsCoord = new HashSet<PidReader.RawLine>();
        foreach (var l in horizontal)
        {
            if (Units.ParsePosition(l.T) is { } p && Regex.Matches(l.T, @"[ENU]\s*-?\d").Count == 3)
            {
                sh.Coords.Add(new GaCoord(p.E, p.N, p.U, l.X0, l.Y0, l.T.Contains('\'') || l.T.Contains('"'), l.T)); usedAsCoord.Add(l);
            }
        }
        foreach (var e in horizontal.Where(l => !usedAsCoord.Contains(l) && Regex.IsMatch(l.T, @"^E\s*[:=]?\s*\S")))
        {
            var n = horizontal.FirstOrDefault(l => Regex.IsMatch(l.T, @"^N\s*[:=]?\s*\S") && Math.Abs(l.X0 - e.X0) < 4 && l.Y0 - e.Y0 is > 4 and < 18);
            var u = n is null ? null : horizontal.FirstOrDefault(l => Regex.IsMatch(l.T, @"^U\s*[:=]?\s*\S") && Math.Abs(l.X0 - e.X0) < 4 && l.Y0 - n.Y0 is > 4 and < 18);
            if (n is null || u is null) continue;
            var ev = Number(AxisRx().Match(e.T).Groups[2].Value, out var i1); var nv = Number(AxisRx().Match(n.T).Groups[2].Value, out var i2); var uv = Number(AxisRx().Match(u.T).Groups[2].Value, out var i3);
            if (ev is null || nv is null || uv is null) continue;
            sh.Coords.Add(new GaCoord(ev.Value, nv.Value, uv.Value, e.X0, e.Y0, i1 || i2 || i3, $"{e.T} {n.T} {u.T}"));
            usedAsCoord.Add(e); usedAsCoord.Add(n); usedAsCoord.Add(u);
        }
        foreach (var l in horizontal)
        {
            if (usedAsCoord.Contains(l)) continue;
            foreach (Match m in ElevRx().Matches(l.T))
            {
                var raw = m.Groups[1].Value.Replace(" ", "");
                var v = Number(raw + m.Groups[2].Value, out var imp);
                if (v is { } mm) sh.Elevations.Add(new GaElevation(mm, l.X0, l.Y0, imp, m.Value.Trim()));
            }
            if (Regex.IsMatch(l.T, @"^(E|N|U|EL)\b")) continue;   // coordinate and elevation text is not a tag
            var tagMatches = TagRx().Matches(l.T);
            foreach (Match m in tagMatches) sh.Tags.Add(new GaTag(m.Value, ModelSnapshot.TagKey(m.Value), (l.X0 + l.X1) / 2, (l.Y0 + l.Y1) / 2));
            foreach (Match m in LooseRx().Matches(l.T))
                if (!tagMatches.Any(t => t.Index <= m.Index && m.Index < t.Index + t.Length) && Regex.IsMatch(m.Value, @"\d") && Regex.IsMatch(m.Value, "[A-Z]"))
                    sh.Loose.Add(new GaTag(m.Value, ModelSnapshot.TagKey(m.Value), (l.X0 + l.X1) / 2, (l.Y0 + l.Y1) / 2));
            if (sh.Scale.Length == 0 && ScaleRx().Match(l.T) is { Success: true } sm) sh.Scale = Regex.Replace(sm.Groups[1].Value, @"\s+", "");
            if (sh.Revision.Length == 0 && RevRx().Match(l.T) is { Success: true } rm) sh.Revision = rm.Groups[1].Value.ToUpperInvariant();
            if (Regex.IsMatch(l.T, @"NORTH", RegexOptions.IgnoreCase) || l.T == "N") sh.HasNorthArrow = true;
            if (Regex.Matches(l.T, "[A-Za-z]{2,}").Count >= 4) sh.Notes++;
        }
        return sh;
    }
}
